"""P5-1.11: packaging detectors always run (stage + finished zip) and the default model is pinned (URL rev + SHA-256)."""
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PKG = (ROOT / "tools" / "package_release.ps1").read_text(encoding="utf-8")
MM = (ROOT / "KSPChatMod" / "ModelManager.cs").read_text(encoding="utf-8")


def test_detectors_unconditional_and_rescan_zip():
    for needle in ("KSP would try to load these as plugins", "must not bundle model weights", "Disallowed DLLs in package",
                   "Package detector failed on zip"):
        assert needle in PKG
    # detectors are not behind a switch
    assert not re.search(r"if\s*\(\s*-not\s+\$Skip\w*\s*\)\s*\{[^}]*Disallowed", PKG)


def test_model_pinned():
    assert re.search(r'DefaultSha256 = "[0-9a-f]{64}"', MM)
    assert re.search(r'DefaultRevision = "[0-9a-f]{40}"', MM)
    assert "/resolve/\" + DefaultRevision" in MM
    assert "new ModelManager(root, DefaultSha256" in MM
