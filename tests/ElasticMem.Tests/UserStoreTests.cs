using ElasticMem.Core.Backend.Vmm;
static class UserStoreTests
{
    // Portable stand-in for ntdll XPRESS (Windows-only): byte RLE as (count,value) pairs.
    sealed class RleCodec:IPageCodec
    {
        public bool CorruptNext;
        public string Name=>"test_rle";
        public bool TryCompress(ReadOnlySpan<byte> page,Span<byte> dst,out int written)
        {
            written=0;int i=0;
            while(i<page.Length){int run=1;while(i+run<page.Length&&run<255&&page[i+run]==page[i])run++;if(written+2>dst.Length||written+2>=4096){written=0;return false;}dst[written++]=(byte)run;dst[written++]=page[i];i+=run;}
            return true;
        }
        public bool TryDecompress(ReadOnlySpan<byte> src,Span<byte> page,out int written)
        {
            written=0;if(src.Length%2!=0)return false;
            for(int i=0;i<src.Length;i+=2){int run=src[i];if(written+run>page.Length)return false;page.Slice(written,run).Fill(src[i+1]);written+=run;}
            if(CorruptNext){page[0]^=1;CorruptNext=false;}
            return true;
        }
    }
    sealed class Backing:IVmPageBacking,IVmQuiescence
    {
        public byte[][] ram;public bool[] mapped,committed;bool paused;
        public Backing(int n){ram=new byte[n][];mapped=new bool[n];committed=new bool[n];for(int i=0;i<n;i++){ram[i]=new byte[4096];Array.Fill(ram[i],(byte)(i+1));mapped[i]=committed[i]=true;}}
        sealed class Lease:IVmPause{readonly Backing b;public Lease(Backing b){this.b=b;b.paused=true;}public void KeepPausedOnFailure(){}public void Dispose(){b.paused=false;}}
        public IVmPause PauseCpuAndIo()=>new Lease(this);public bool IsEvictable(int p)=>true;
        void G(){if(!paused)throw new InvalidOperationException("no barrier");}
        public byte[] Read(int p){G();return (byte[])ram[p].Clone();}public void Write(int p,byte[] b){G();ram[p]=(byte[])b.Clone();}
        public void Unmap(int p){G();mapped[p]=false;}public void Map(int p){G();mapped[p]=true;}
        public void Decommit(int p){G();committed[p]=false;ram[p]=new byte[4096];}public void Commit(int p){G();committed[p]=true;}
    }
    public static int Run()
    {
        int checks=0,fails=0;void C(bool ok,string m){checks++;if(!ok){fails++;Console.WriteLine("FAIL USER STORE: "+m);}}
        bool Throws<T>(Action a)where T:Exception{try{a();return false;}catch(T){return true;}}
        var codec=new RleCodec();
        using(var s=new UserPageStore(codec,16,1<<20))
        {
            var page=new byte[4096];Array.Fill(page,(byte)7);
            var r=s.Put(1,page);r.Validate();C(r.AllocationBytes==r.PayloadBytes+24,"ledger rule matches SYS");
            C(s.Get(1,r.Generation).AsSpan().SequenceEqual(page),"roundtrip");
            C(Throws<InvalidDataException>(()=>s.Get(1,r.Generation+1)),"stale generation rejected");
            var r2=s.Put(1,page);C(r2.Generation>r.Generation&&Throws<InvalidDataException>(()=>s.Get(1,r.Generation)),"overwrite bumps generation, old token dead");
            C(s.Stats().Pages==1&&s.Stats().LedgerBytes==r2.AllocationBytes,"overwrite does not leak ledger");
            var noisy=new byte[4096];new Random(3).NextBytes(noisy);
            C(Throws<PageNotBeneficialException>(()=>s.Put(2,noisy)),"incompressible page rejected");
            C(Throws<KeyNotFoundException>(()=>s.Get(2,1)),"rejected page not stored");
            C(s.Get(1,r2.Generation).AsSpan().SequenceEqual(page),"rejection keeps other entries");
            codec.CorruptNext=true;C(Throws<InvalidDataException>(()=>s.Get(1,r2.Generation)),"CRC catches corruption");
            C(s.Delete(1,r2.Generation)&&!s.Delete(1,r2.Generation)&&s.Stats().LedgerBytes==0,"delete frees ledger");
            C(Throws<ArgumentOutOfRangeException>(()=>s.Put(16,page)),"key bound");
        }
        using(var tiny=new UserPageStore(codec,4,4096))
        {
            var page=new byte[4096];for(int i=0;i<4096;i++)page[i]=(byte)(i/64); // 64 runs -> 128 bytes payload
            int ok=0;var gens=new ulong[4];for(uint k=0;k<4;k++){try{gens[k]=tiny.Put(k,page).Generation;ok++;}catch(IOException ex)when(ex is not PageNotBeneficialException){}}
            C(ok==4&&tiny.Stats().LedgerBytes<=4096,"quota accounting");
            var big=new byte[4096];for(int i=0;i<4096;i++)big[i]=(byte)(i/3); // 1366 runs -> 2732 bytes payload
            var g1=tiny.Put(0,big);C(tiny.Stats().LedgerBytes==3*152+2756,"replacement ledger exact");
            var before=tiny.Stats();
            C(Throws<IOException>(()=>tiny.Put(1,big))&&tiny.Stats()==before,"quota failure keeps old value and ledger");
            C(tiny.Get(1,gens[1]).AsSpan().SequenceEqual(page)&&g1.Generation>gens[3],"old page intact after quota failure");
        }
        // Provider + user store end-to-end on a model backing: evict all, restore all, bytes identical.
        var b=new Backing(8);using(var store=new UserPageStore(codec,8,1<<20))
        {
            var provider=new VmPageProvider(8,b,store,b);var router=new VmMemoryExitRouter(provider,0x100000,8);
            for(int p=0;p<8;p++)provider.Evict(p);
            C(b.committed.All(x=>!x)&&b.mapped.All(x=>!x)&&store.Stats().Pages==8,"all pages evicted to user store");
            for(int p=7;p>=0;p--)C(router.RestoreForUnmappedGpa(0x100000+(ulong)p*4096+123),"fault restores page "+p);
            C(Enumerable.Range(0,8).All(p=>b.ram[p].All(x=>x==(byte)(p+1))&&b.mapped[p]&&b.committed[p]),"restored bytes identical");
            C(store.Stats().Pages==0&&store.Stats().LedgerBytes==0,"restored entries deleted");
        }
        Console.WriteLine($"USER STORE {checks} checks; portable codec model, NOT native ntdll XPRESS");
        return fails;
    }
}
