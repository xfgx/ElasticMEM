import importlib.util,unittest,tempfile,pathlib,json,subprocess,zipfile,hashlib,struct
module=pathlib.Path(__file__).with_name('publish-test1-server.py');spec=importlib.util.spec_from_file_location('publisher',module);pub=importlib.util.module_from_spec(spec);spec.loader.exec_module(pub)
sha=lambda b:hashlib.sha256(b).hexdigest()
class Tests(unittest.TestCase):
 def setUp(self):
  self.tmp=tempfile.TemporaryDirectory();self.base=pathlib.Path(self.tmp.name);self.inc=self.base/'incoming';self.inc.mkdir();self.up=self.base/'updates';self.up.mkdir();self.old=json.dumps({'version':'0.9.0','url':'old.zip'}).encode();(self.up/'latest.json').write_bytes(self.old);(self.up/'old.zip').write_bytes(b'old retained');self.v='1.0.0'
  repo=self.base/'repo';repo.mkdir();subprocess.run(['git','init','-q',str(repo)],check=True);subprocess.run(['git','-C',str(repo),'config','user.email','test@local.invalid'],check=True);subprocess.run(['git','-C',str(repo),'config','user.name','Test'],check=True);(repo/'test.txt').write_text('fixture');subprocess.run(['git','-C',str(repo),'add','.'],check=True);subprocess.run(['git','-C',str(repo),'commit','-qm','fixture'],check=True);self.commit=subprocess.check_output(['git','-C',str(repo),'rev-parse','HEAD'],text=True).strip();subprocess.run(['git','-C',str(repo),'tag','elasticmem-app-v1.0.0'],check=True);bundle=self.base/'test.bundle';subprocess.run(['git','-C',str(repo),'bundle','create',str(bundle),'--all'],check=True,stdout=subprocess.DEVNULL)
  exe=bytearray(256);struct.pack_into('<I',exe,0x3c,128);exe[128:132]=b'PE\0\0';struct.pack_into('<H',exe,132,0x8664);guide=b'full instructions fixture';qa={'code_commit':self.commit,'core_suite':'passed'};files={'ElasticMem.exe':bytes(exe),'CHECK-ALL.cmd':b'fixture','ПОЛНАЯ-ИНСТРУКЦИЯ.md':guide,'QA.json':json.dumps(qa).encode()};build={'version':self.v,'code_commit':self.commit,'source_dirty':False,'files':[{'path':n,'bytes':len(b),'sha256':sha(b)} for n,b in files.items()]}
  binary=self.inc/'ElasticMem-v1.0.0-win-x64.zip'
  with zipfile.ZipFile(binary,'w') as z:
   for n,b in files.items():z.writestr(n,b)
   z.writestr('BUILD.json',json.dumps(build))
  source=self.inc/'ElasticMem-v1.0.0-source.zip'
  with zipfile.ZipFile(source,'w') as z:z.writestr('ElasticMem/ElasticMem.git.bundle',bundle.read_bytes());z.writestr('ElasticMem/QA.json',json.dumps(qa))
  gd=self.inc/'ElasticMem-v1.0.0-guide.md';gd.write_bytes(guide);cs=self.inc/'ElasticMem-v1.0.0-SHA256.txt';cs.write_text(''.join(sha(p.read_bytes())+'  '+p.name+'\n' for p in [binary,source,gd]));self.request={'version':self.v,'code_commit':self.commit,'expected_latest_sha256':sha(self.old),'notes':'fixture','files':[{'name':p.name,'bytes':p.stat().st_size,'sha256':sha(p.read_bytes())} for p in [binary,source,gd,cs]]}
 def tearDown(self):self.tmp.cleanup()
 def unchanged(self):self.assertEqual((self.up/'latest.json').read_bytes(),self.old)
 def test_success_keeps_old(self):
  result=pub.publish(self.request,self.base);self.assertEqual(result['published'],self.v);self.assertEqual((self.up/'old.zip').read_bytes(),b'old retained');self.assertTrue(list((self.base/'backups').glob('*.json')))
 def test_hash_tamper_refused(self):
  (self.inc/self.request['files'][0]['name']).write_bytes(b'tampered')
  with self.assertRaises(ValueError):pub.publish(self.request,self.base)
  self.unchanged()
 def test_concurrent_latest_refused(self):
  self.request['expected_latest_sha256']='0'*64
  with self.assertRaises(ValueError):pub.publish(self.request,self.base)
  self.unchanged()
 def test_no_downgrade(self):
  (self.up/'latest.json').write_text(json.dumps({'version':'2.0.0'}));self.request['expected_latest_sha256']=sha((self.up/'latest.json').read_bytes())
  with self.assertRaises(ValueError):pub.publish(self.request,self.base)
  self.assertEqual(json.loads((self.up/'latest.json').read_text())['version'],'2.0.0')
 def test_extra_private_file_refused(self):
  self.request['files'].append({'name':'logs.zip','bytes':0,'sha256':'0'*64})
  with self.assertRaises(ValueError):pub.publish(self.request,self.base)
  self.unchanged()
 def test_clobber_refused(self):
  (self.up/self.request['files'][0]['name']).write_bytes(b'existing')
  with self.assertRaises(ValueError):pub.publish(self.request,self.base)
  self.unchanged()
if __name__=='__main__':unittest.main()
