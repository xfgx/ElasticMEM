import pathlib,tempfile,unittest,json
from release_inventory import BASE,inventory,verify_build
class InventoryTests(unittest.TestCase):
 def setUp(self):
  self.tmp=tempfile.TemporaryDirectory();self.root=pathlib.Path(self.tmp.name)
  for n in BASE:
   p=self.root/n;p.parent.mkdir(parents=True,exist_ok=True);p.write_bytes(b'approved')
  self.build={'files':inventory(self.root,BASE)};(self.root/'BUILD.json').write_text(json.dumps(self.build))
 def tearDown(self):self.tmp.cleanup()
 def test_clean(self):verify_build(self.root,self.build)
 def test_dump(self):
  (self.root/'memory.bin').write_bytes(b'private')
  with self.assertRaises(ValueError):verify_build(self.root,self.build)
 def test_log(self):
  (self.root/'user.log').write_bytes(b'private')
  with self.assertRaises(ValueError):verify_build(self.root,self.build)
 def test_symlink(self):
  (self.root/'ElasticMem.pdb').unlink();(self.root/'ElasticMem.pdb').symlink_to('/etc/hosts')
  with self.assertRaises(ValueError):verify_build(self.root,self.build)
 def test_regenerated_inventory(self):
  (self.root/'user.log').write_bytes(b'private');self.build['files'].append({'path':'user.log'})
  with self.assertRaises(ValueError):verify_build(self.root,self.build)
 def test_changed(self):
  (self.root/'ElasticMem.exe').write_bytes(b'changed')
  with self.assertRaises(ValueError):verify_build(self.root,self.build)
unittest.main()
