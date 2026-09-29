import hashlib
import importlib.util
import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest import mock

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "sheetmusic-worker.py"
spec = importlib.util.spec_from_file_location("sheetmusic_worker", SCRIPT)
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)

PIECE_ID = "mutopia:BachJS/BWV999/score"
PDF_URL = "https://www.mutopiaproject.org/ftp/BachJS/BWV999/score-a4.pdf"


class SheetMusicWorkerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.config = self.root / "config"
        self.queue = self.config / "sheetmusic-queue"
        self.queue.mkdir(parents=True)
        self.books = self.root / "books"
        self.spool = self.root / "spool"
        self.old = (worker.CONFIG, worker.QUEUE, worker.BOOKS, worker.SPOOL)
        worker.CONFIG, worker.QUEUE = self.config, self.queue
        worker.BOOKS, worker.SPOOL = self.books, self.spool
        (self.config / "sheetmusic-catalog.json").write_text(json.dumps([{
            "id": "BachJS/BWV999/score", "pdf_url": PDF_URL,
        }]))
        self.job = {
            "id": "a" * 32, "userId": "b" * 32, "pieceId": PIECE_ID,
            "title": "Prelude", "composer": "BachJS", "license": "Public Domain",
            "sourceUrl": "https://www.mutopiaproject.org/ftp/BachJS/BWV999/score.ly",
            "pdfUrl": PDF_URL, "status": "queued",
        }
        self.path = self.queue / (self.job["id"] + ".json")
        self.path.write_text(json.dumps(self.job))
        self.path.chmod(0o640)

    def tearDown(self):
        worker.CONFIG, worker.QUEUE, worker.BOOKS, worker.SPOOL = self.old
        self.temp.cleanup()

    def test_trusted_request_downloads_imports_and_becomes_ready(self):
        payload = b"%PDF-1.4\nscore fixture"

        def fake_download(url, source, target):
            self.assertEqual((url, source), (PDF_URL, "mutopia"))
            target.write_bytes(payload)
            return hashlib.sha256(payload).hexdigest()

        with mock.patch.object(worker, "_public_host", return_value=True), \
             mock.patch.object(worker, "_download", side_effect=fake_download), \
             mock.patch.object(worker, "_trigger_scan") as scan, \
             mock.patch.object(worker, "_find_item", return_value="c" * 32):
            worker._process(self.path)
            importing = json.loads(self.path.read_text())
            self.assertEqual(importing["status"], "importing")
            self.assertEqual(self.path.stat().st_mode & 0o777, 0o640)
            self.assertTrue(importing["filePath"].startswith("/media/sheetmusic/"))
            scan.assert_called_once()
            self.assertEqual(len(list(self.books.rglob("*.pdf"))), 1)
            opf = next(self.books.rglob("metadata.opf")).read_text()
            self.assertIn("Public Domain", opf)
            self.assertIn(self.job["sourceUrl"], opf)
            worker._process(self.path)
            ready = json.loads(self.path.read_text())
            self.assertEqual(ready["status"], "ready")
            self.assertEqual(ready["itemId"], "c" * 32)

    def test_rejects_untrusted_url_without_writing_media(self):
        self.job["pdfUrl"] = "https://private.invalid/x.pdf"
        self.path.write_text(json.dumps(self.job))
        with self.assertRaisesRegex(ValueError, "untrusted score URL"):
            worker._process(self.path)
        self.assertFalse(self.books.exists())

    def test_archive_id_and_download_url_must_match(self):
        self.job.update(pieceId="ia:imslp-example", pdfUrl=
            "https://archive.org/download/other/score.pdf",
            sourceUrl="https://archive.org/details/imslp-example")
        self.path.write_text(json.dumps(self.job))
        with mock.patch.object(worker, "_public_host", return_value=True):
            with self.assertRaisesRegex(ValueError, "do not match"):
                worker._process(self.path)

    def test_stale_import_releases_the_queue_slot(self):
        self.job.update(status="importing", filePath="/media/sheetmusic/stale/score.pdf",
            createdAt="2020-01-01T00:00:00+00:00")
        self.path.write_text(json.dumps(self.job))
        worker._process(self.path)
        result = json.loads(self.path.read_text())
        self.assertEqual(result["status"], "error")
        self.assertEqual(result["error"], "ImportTimeout")


if __name__ == "__main__":
    unittest.main()
