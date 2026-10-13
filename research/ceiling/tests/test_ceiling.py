import csv, itertools, json, math, sys, tempfile, unittest, shutil, subprocess
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import g0, run, verify, page_idle
META={'schema':'elasticmem.events.v1','trace_kind':'synthetic','source':'unit fixture','scope':'unit pages','limitations':[], 'clock':'synthetic monotonic ns','event_loss_count':0,'access_definition':'every page touch','process_identity':'unique IDs','page_size':4096,'start_ns':0,'end_ns':1000000}
class CeilingTests(unittest.TestCase):
    def trace(self,text,meta=None):
        d=tempfile.TemporaryDirectory(); self.addCleanup(d.cleanup);p=Path(d.name)/'trace.csv';p.write_text('timestamp_ns,pid,page,op\n'+text)
        return g0.load_trace(p,meta or META)
    def test_lifecycle_page_time(self):
        intervals,stats,n=self.trace('0,1,0x1000,map\n100000,1,0x1000,read\n900000,1,0x1000,write\n1000000,1,0x1000,unmap\n')
        self.assertEqual(intervals,[(1,100000,True,'initial'),(1,800000,True,'interior'),(1,100000,False,'unmap_tail')])
        self.assertEqual(stats[1]['page_time_ns'],1000000); self.assertEqual(stats[1]['accesses'],2);self.assertEqual(n,4)
    def test_reused_address_lifetimes(self):
        _,s,_=self.trace('0,1,4096,map\n200000,1,4096,unmap\n800000,1,4096,map\n1000000,1,4096,unmap\n')
        self.assertEqual(s[1]['page_time_ns'],400000)
    def test_live_at_end(self):
        i,s,_=self.trace('0,1,4096,map\n100000,1,4096,read\n')
        self.assertEqual(s[1]['page_time_ns'],1000000);self.assertEqual(i[-1],(1,900000,False,'censored_tail'))
    def test_bruteforce_global_optimum(self):
        intervals=[(1,100000,True,'interior'),(1,500000,True,'interior'),(2,250000,True,'interior'),(2,200000,False,'unmap_tail')]
        stats={1:{'page_time_ns':1000000,'accesses':2},2:{'page_time_ns':1000000,'accesses':1}}
        result=g0.envelope(intervals,stats,META,10,20,[0,10,20],30,'all')
        for curve in result['curves']:
            k=min(3,math.floor(curve['mean_stall_budget_us']*3/20+1e-12))
            recoverable=[max(0,gap-30000) for _,gap,need,kind in intervals if need]
            brute=max(sum(x) for x in itertools.combinations(recoverable,k))+190000
            self.assertAlmostEqual(curve['aggregate']['saved_fraction_of_mapped_page_time'],brute/2000000)
            self.assertAlmostEqual(sum(x['optimistic_mean_saved_bytes'] for x in curve['processes']),curve['aggregate']['optimistic_mean_saved_bytes'])
    def test_max_fault_rejects_recoveries(self):
        i,s,_=self.trace('0,1,4096,map\n900000,1,4096,read\n')
        r=g0.envelope(i,s,META,10,100,[100],99);self.assertEqual(r['curves'][0]['aggregate']['modeled_recoveries'],0)
    def test_monotonic_and_bounded(self):
        i,s,_=self.trace('0,1,4096,map\n100000,1,4096,read\n900000,1,4096,read\n')
        r=g0.envelope(i,s,META,10,100,[0,1,10,100,1000],100)
        values=[x['aggregate']['saved_fraction_of_mapped_page_time'] for x in r['curves']]
        self.assertEqual(values,sorted(values));self.assertTrue(all(0<=v<=1 for v in values))
    def test_budget_charged(self):
        i,s,_=self.trace('0,1,4096,map\n100000,1,4096,read\n900000,1,4096,read\n')
        r=g0.envelope(i,s,META,10,100,[0,10,49,50],100)
        for c in r['curves']:self.assertLessEqual(c['aggregate']['modeled_mean_added_us_per_access'],c['mean_stall_budget_us'])
    def test_sampled_refused(self):
        with self.assertRaisesRegex(ValueError,'Sampled'):self.trace('0,1,4096,map\n',dict(META,trace_kind='sampled'))
    def test_missing_provenance(self):
        m=dict(META);del m['scope']
        with self.assertRaises(ValueError):self.trace('0,1,4096,map\n',m)
    def test_unmapped_access(self):
        with self.assertRaisesRegex(ValueError,'without map'):self.trace('1,1,4096,read\n')
    def test_duplicate_map(self):
        with self.assertRaisesRegex(ValueError,'Duplicate'):self.trace('0,1,4096,map\n0,1,4096,map\n')
    def test_unsorted(self):
        with self.assertRaises(ValueError):self.trace('5,1,4096,map\n4,1,4096,read\n')
    def test_unaligned(self):
        with self.assertRaises(ValueError):self.trace('0,1,4097,map\n')
    def test_invalid_cost(self):
        for value in (-1,float('nan'),float('inf')):
            with self.assertRaises(ValueError):g0.envelope([], {1:{'page_time_ns':1,'accesses':1}},META,0,value,[0],100)
    def test_probe_never_full_trace(self):
        r=page_idle.probe();self.assertFalse(r['full_trace_available']);self.assertEqual(r['trace_kind'],'sampled')
    def test_hash_verification_detects_change(self):
        with tempfile.TemporaryDirectory() as d:
            p=Path(d);(p/'raw.txt').write_text('before')
            (p/'manifest.json').write_text(json.dumps({'artifacts':[{'path':'raw.txt','bytes':6,'sha256':run.digest(p/'raw.txt')}],'inputs':[],'status':'fixture','commit':'fixture'}))
            self.assertEqual(verify.verify(p)['files'],1)
            (p/'raw.txt').write_text('after!')
            with self.assertRaisesRegex(ValueError,'Hash mismatch'):verify.verify(p)
    def test_g3_summary_coverage_and_quantiles(self):
        with tempfile.TemporaryDirectory() as d:
            p=Path(d);(p/'status.json').write_text(json.dumps({'status':'measured','limitations':['fixture, not measurements']}))
            with (p/'samples.csv').open('w',newline='') as f:
                w=csv.writer(f);w.writerow(['mode','bytes','iteration','warmup','total_us','transfer_us','materialize_us','byte_match'])
                for mode in ('gpu_write','gpu_restore','disk_restore'):
                    for size in (4096,65536,1048576,16777216):
                        for rep in range(20):w.writerow([mode,size,rep,int(rep<10),rep+1,rep+1,0,1])
            result=run.summarize_g3(p,10);self.assertEqual(len(result),12);self.assertEqual(result[0]['p50_us'],15.5)
            self.assertAlmostEqual(result[0]['p95_us'],19.55)
            with self.assertRaises(ValueError):run.summarize_g3(p,11)
            with (p/'samples.csv').open(newline='') as f: rows=list(csv.DictReader(f))
            for row in rows: row['warmup']=str(1-int(row['warmup']))
            with (p/'samples.csv').open('w',newline='') as f:
                w=csv.DictWriter(f,fieldnames=list(rows[0]));w.writeheader();w.writerows(rows)
            with self.assertRaisesRegex(ValueError,'Warmup'):run.summarize_g3(p,10)
    def test_boundaries_excluded_by_default(self):
        i,s,_=self.trace('0,1,4096,map\n100000,1,4096,read\n')
        default=g0.envelope(i,s,META,10,100,[1000],100)
        full=g0.envelope(i,s,META,10,100,[1000],100,'all')
        self.assertEqual(default['curves'][0]['aggregate']['optimistic_mean_saved_bytes'],0)
        self.assertGreater(full['curves'][0]['aggregate']['optimistic_mean_saved_bytes'],0)
    def test_known_loss_refused(self):
        with self.assertRaises(ValueError):self.trace('0,1,4096,map\n',dict(META,event_loss_count=1))
    def test_cost_monotonic(self):
        i,s,_=self.trace('0,1,4096,map\n100000,1,4096,read\n900000,1,4096,read\n')
        vals=[g0.envelope(i,s,META,10,r,[1000],1000)['curves'][0]['aggregate']['optimistic_mean_saved_bytes'] for r in (10,50,100,500)]
        self.assertEqual(vals,sorted(vals,reverse=True))
    def test_deterministic_replay(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);base=Path(__file__).resolve().parents[1]/'examples'
            g0.analyze(base/'trace.csv',base/'metadata.json',root/'a')
            g0.analyze(base/'trace.csv',base/'metadata.json',root/'b')
            self.assertEqual((root/'a'/'g0.json').read_bytes(),(root/'b'/'g0.json').read_bytes())
            self.assertEqual((root/'a'/'curve.csv').read_bytes(),(root/'b'/'curve.csv').read_bytes())
    def test_empty_and_unknown_op(self):
        with self.assertRaises(ValueError):self.trace('')
        with self.assertRaises(ValueError):self.trace('0,1,4096,map\n1,1,4096,load\n')
    def test_gap_shorter_than_transfer(self):
        i,s,_=self.trace('0,1,4096,map\n100000,1,4096,read\n101000,1,4096,read\n')
        r=g0.envelope(i,s,META,10,100,[1000],100)
        self.assertEqual(r['curves'][0]['aggregate']['optimistic_mean_saved_bytes'],0)
    def test_runner_clean_dirty_duplicate_and_verification(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);target=root/'research'/'ceiling'
            shutil.copytree(Path(__file__).resolve().parents[1],target,ignore=shutil.ignore_patterns('runs','__pycache__'))
            (root/'.gitignore').write_text('__pycache__/\nresearch/ceiling/runs/\n')
            def git_cmd(*args):return subprocess.run(['git','-C',str(root),*args],capture_output=True,text=True,check=True)
            git_cmd('init','-q');git_cmd('config','user.name','test');git_cmd('config','user.email','test@local.invalid');git_cmd('add','.');git_cmd('commit','-qm','fixture')
            command=[sys.executable,str(target/'run.py'),'--id','fixture-probe','probe']
            first=subprocess.run(command,capture_output=True,text=True);self.assertEqual(first.returncode,0,first.stderr)
            m=json.loads((target/'runs'/'fixture-probe'/'manifest.json').read_text());self.assertEqual(m['status'],'capability_probe_only')
            verified=subprocess.run([sys.executable,str(target/'verify.py'),str(target/'runs'/'fixture-probe'),'--check-git'],capture_output=True,text=True)
            self.assertEqual(verified.returncode,0,verified.stderr)
            self.assertNotEqual(subprocess.run(command,capture_output=True).returncode,0)
            (root/'untracked-source.txt').write_text('dirty')
            rejected=subprocess.run([sys.executable,str(target/'run.py'),'--id','dirty-probe','probe'],capture_output=True,text=True)
            self.assertNotEqual(rejected.returncode,0);self.assertIn('dirty tree refused',rejected.stderr)
            self.assertFalse((target/'runs'/'dirty-probe').exists())
    def test_extra_csv_field_refused(self):
        with self.assertRaisesRegex(ValueError,'Malformed CSV'):self.trace('0,1,4096,map,EXTRA\n')
    def test_invalid_metadata_shape(self):
        with self.assertRaises(ValueError):self.trace('0,1,4096,map\n',dict(META,limitations='not a list'))
    def test_empty_budgets_and_zero_eviction(self):
        stats={1:{'page_time_ns':1,'accesses':1}}
        with self.assertRaises(ValueError):g0.envelope([],stats,META,10,100,[],100)
        with self.assertRaises(ValueError):g0.envelope([],stats,META,0,100,[0],100)
    def test_verifier_nested_manifest_and_paths(self):
        with tempfile.TemporaryDirectory() as d:
            p=Path(d);(p/'raw').mkdir();(p/'raw'/'manifest.json').write_text('nested')
            item={'path':'raw/manifest.json','bytes':6,'sha256':run.digest(p/'raw'/'manifest.json')}
            m={'artifacts':[item],'inputs':[],'status':'fixture','commit':'fixture'}
            (p/'manifest.json').write_text(json.dumps(m));self.assertEqual(verify.verify(p)['files'],1)
            m['artifacts']=[item,item];(p/'manifest.json').write_text(json.dumps(m))
            with self.assertRaisesRegex(ValueError,'Duplicate'):verify.verify(p)
            m['artifacts']=[item];m['inputs']=[dict(item,path='../outside')];(p/'manifest.json').write_text(json.dumps(m))
            with self.assertRaisesRegex(ValueError,'Unsafe'):verify.verify(p)
            m['inputs']=[];(p/'manifest.json').write_text(json.dumps(m));(p/'raw'/'manifest.json').write_text('changed')
            with self.assertRaises(ValueError):verify.verify(p)
    def test_deleted_tag_still_seals_invalid_run(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d);(root/'.gitignore').write_text('runs/\n')
            for args in [('init','-q'),('config','user.name','test'),('config','user.email','test@local.invalid'),('add','.'),('commit','-qm','fixture')]:
                subprocess.run(['git','-C',str(root),*args],check=True,capture_output=True)
            def remove_tag():
                subprocess.run(['git','-C',str(root),'tag','-d','run/deleted-tag'],check=True,capture_output=True)
                return {'test':'tag deletion fixture'}
            with patch.object(run,'ROOT',root),patch.object(page_idle,'probe',side_effect=remove_tag),patch.object(sys,'argv',['run.py','--id','deleted-tag','--out-root',str(root/'runs'),'probe']):
                self.assertEqual(run.main(),1)
            m=json.loads((root/'runs'/'deleted-tag'/'manifest.json').read_text())
            self.assertEqual(m['status'],'invalid_source_changed_during_run')
            self.assertIn(m['status'],(root/'runs'/'deleted-tag'/'report.md').read_text())
    def test_no_access_trace(self):
        i,s,_=self.trace('0,1,4096,map\n1000000,1,4096,unmap\n')
        r=g0.envelope(i,s,META,10,100,[0],100);self.assertEqual(r['curves'][0]['aggregate']['modeled_recoveries'],0)
if __name__=='__main__':unittest.main()
