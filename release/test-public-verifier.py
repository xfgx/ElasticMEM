import importlib.util,pathlib,unittest
s=importlib.util.spec_from_file_location('verifier',pathlib.Path(__file__).with_name('verify-public.py'));v=importlib.util.module_from_spec(s);s.loader.exec_module(v)
class ManifestTests(unittest.TestCase):
 def setUp(self):
  self.m={'version':'0.15.0','codeCommit':'a'*40,'sha256':'b'*64};self.r={'version':'0.15.0','code_commit':'a'*40,'files':[{'name':'ElasticMem-v0.15.0-win-x64.zip','sha256':'b'*64}]}
 def test_valid(self):v.validate_manifest(self.m,self.r)
 def test_missing(self):
  del self.m['sha256']
  with self.assertRaises(AssertionError):v.validate_manifest(self.m,self.r)
 def test_wrong(self):
  self.m['sha256']='c'*64
  with self.assertRaises(AssertionError):v.validate_manifest(self.m,self.r)
 def test_malformed(self):
  self.m['sha256']='b'*63
  with self.assertRaises(AssertionError):v.validate_manifest(self.m,self.r)
unittest.main()
