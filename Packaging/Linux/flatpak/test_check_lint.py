import contextlib
import io
from pathlib import Path
import runpy
import subprocess
import unittest
from unittest.mock import patch


class LintPolicyTests(unittest.TestCase):
    def check(self, output, code=1):
        result = subprocess.CompletedProcess([], code, stdout=output, stderr='')
        with patch('subprocess.run', return_value=result), contextlib.redirect_stdout(io.StringIO()):
            runpy.run_path(str(Path(__file__).with_name('check-lint.py')))

    def test_clean_result(self):
        self.check('{}', 0)

    def test_only_home_permission_is_accepted(self):
        self.check('{"errors":["finish-args-home-filesystem-access"]}')

    def test_other_error_is_rejected(self):
        with self.assertRaises(SystemExit):
            self.check('{"errors":["finish-args-home-filesystem-access","appstream-screenshots-missing"]}')

    def test_warning_is_rejected(self):
        with self.assertRaises(SystemExit):
            self.check('{"warnings":["unexpected-permission"]}', 0)

    def test_unexplained_failure_is_rejected(self):
        with self.assertRaises(SystemExit):
            self.check('{}')

    def test_invalid_json_is_rejected(self):
        with self.assertRaises(ValueError):
            self.check('not a linter report')


if __name__ == '__main__':
    unittest.main()
