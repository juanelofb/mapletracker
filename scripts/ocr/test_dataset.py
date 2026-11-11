import base64
import json
import struct
import tempfile
import unittest
from pathlib import Path

import dataset


class DatasetChecks(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.root = self.directory / "dataset"

    def image(self, name, width=1):
        # Real PNG bytes; the modified width is only used by metadata-only tests.
        data = bytearray(base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aBisAAAAASUVORK5CYII="))
        data[16:20] = struct.pack(">I", width)
        path = self.directory / (name + ".png")
        path.write_bytes(data)
        return path

    def add(self, number, text=None, group=None, reviewed=True):
        return dataset.add(self.root, self.image(str(number), number), text or f"S> Item {number}",
                           group or f"session-{number}", "chat", reviewed)

    def test_review_and_deduplication(self):
        image = self.image("source")
        identifier = dataset.add(self.root, image, "B> GFA 60%", "session", "chat")
        self.assertEqual(0, sum(s["reviewed"] for s in dataset.checked(self.root)))
        self.assertEqual(identifier, dataset.add(self.root, image, "B> GFA 60%", "other", "chat"))
        with self.assertRaises(ValueError):
            dataset.add(self.root, image, "S> GFA 60%", "other", "chat")
        dataset.review(self.root, identifier, "S> GFA 60%")
        self.assertTrue(dataset.checked(self.root)[0]["reviewed"])

    def test_group_and_duplicate_text_leakage(self):
        self.add(1, "S> GFA 60%", "morning")
        self.add(2, "Another line", "morning")
        self.add(3, "S> GFA 60%", "afternoon")
        self.add(4, "Fourth line", "afternoon")
        self.add(5, "Independent", "evening")
        self.add(6, "Unreviewed", "unreviewed", False)
        output = self.directory / "export"
        result = dataset.split(self.root, output)
        self.assertEqual(5, result["train"] + result["test"])
        manifest = json.loads((output / "split.json").read_text())
        linked = [s["split"] for s in manifest["samples"] if s["group"] in ["morning", "afternoon"]]
        self.assertEqual(1, len(set(linked)))
        with self.assertRaises(ValueError):
            dataset.split(self.root, output)
        other = self.directory / "export-again"
        dataset.split(self.root, other)
        self.assertEqual((output / "split.json").read_bytes(), (other / "split.json").read_bytes())

    def test_changed_image_and_bad_transcript(self):
        identifier = self.add(1)
        with self.assertRaises(ValueError):
            dataset.review(self.root, identifier, "two\nlines")
        (self.root / (identifier + ".png")).write_bytes(b"changed")
        with self.assertRaises(ValueError):
            dataset.checked(self.root)

    def test_split_needs_independent_groups(self):
        self.add(1, group="one-session")
        self.add(2, group="one-session")
        with self.assertRaises(ValueError):
            dataset.split(self.root, self.directory / "export")

    def test_metrics_and_report_identity(self):
        identifier = self.add(1, "B> GFA 60%")
        report = dict(Model="eng", ModelSha256="test-model", Psm="SingleLine", Results=[
            dict(Id=identifier, ImageSha256=dataset.digest(self.root / (identifier + ".png")),
                 Text="B> GFA 80%\n", Milliseconds=2)
        ])
        path = self.directory / "report.json"
        dataset.write_json(path, report)
        result = dataset.score(self.root, path)
        self.assertEqual(1, result["character_errors"])
        self.assertEqual(10, result["character_error_percent"])
        self.assertEqual(0, result["exact_lines"])
        self.assertEqual(2, result["p95_milliseconds"])
        report["Results"][0]["ImageSha256"] = "wrong-image"
        dataset.write_json(path, report)
        with self.assertRaises(ValueError):
            dataset.score(self.root, path)
        report["Results"] = []
        dataset.write_json(path, report)
        with self.assertRaises(ValueError):
            dataset.score(self.root, path)

    def test_case_punctuation_and_unicode(self):
        self.assertEqual(1, dataset.distance("B>", "b>"))
        self.assertEqual(1, dataset.distance("60%", "60"))
        self.assertEqual(dataset.normalized("Caf\u00e9"), dataset.normalized("Cafe\u0301"))
        self.assertEqual("a b", dataset.normalized("a  b\n"))


if __name__ == "__main__":
    unittest.main()
