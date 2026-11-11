import base64
import io
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import train_native


class NativeTrainingChecks(unittest.TestCase):
    def test_training_lists_and_boxes_use_lf_on_windows(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            source = root / 'source'
            source.mkdir()
            image = source / 'sample-example.png'
            image.write_bytes(base64.b64decode('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aBisAAAAASUVORK5CYII='))
            image.with_suffix('.gt.txt').write_text('B> 60%\n', encoding='utf-8')

            def fake_run(command, log, environment):
                Path(str(command[2]) + '.lstmf').write_bytes(b'fixture')

            with patch.object(train_native, 'run', side_effect=fake_run) as invoke:
                listing, count = train_native.prepare(source, root / 'prepared', Path('tesseract.exe'),
                                                      root, Path('lstm.train'), io.StringIO(), {}, 'maple_chat')
            command = invoke.call_args.args[0]
            self.assertEqual('maple_chat', command[command.index('-l') + 1])
            self.assertEqual(1, count)
            self.assertNotIn(b'\r', listing.read_bytes())
            box = (root / 'prepared/sample-example.box').read_bytes()
            self.assertNotIn(b'\r', box)
            self.assertTrue(box.endswith(b'\t 0 0 1 1 0\n'))
            self.assertEqual('B> 60%\n', image.with_suffix('.gt.txt').read_text())

    def test_character_set_expansion_requires_separate_workflow(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            source = root / 'source'
            source.mkdir()
            (source / 'sample-example.png').write_bytes(b'not-read-before-validation')
            (source / 'sample-example.gt.txt').write_text('caf\u00e9\n', encoding='utf-8')
            with self.assertRaises(ValueError):
                train_native.prepare(source, root / 'prepared', Path('tesseract.exe'),
                                     root, Path('lstm.train'), io.StringIO(), {})


if __name__ == '__main__':
    unittest.main()
