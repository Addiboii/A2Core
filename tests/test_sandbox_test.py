import unittest
import xml.etree.ElementTree as ET
from tools.prepare_sandbox_test import configuration


class SandboxConfigurationTests(unittest.TestCase):
    def test_network_and_host_access_boundary(self):
        root = ET.fromstring(configuration('C:\\copy & data', 'C:\\scripts', 'C:\\results'))
        self.assertEqual(root.findtext('Networking'), 'Disable')
        self.assertEqual(root.findtext('ClipboardRedirection'), 'Disable')
        folders = root.findall('MappedFolders/MappedFolder')
        self.assertEqual(len(folders), 3)
        self.assertEqual([f.findtext('ReadOnly') for f in folders], ['true', 'true', 'false'])
        self.assertEqual(folders[0].findtext('HostFolder'), 'C:\\copy & data')
        self.assertEqual(folders[2].findtext('SandboxFolder'), 'C:\\AionTest\\Results')
