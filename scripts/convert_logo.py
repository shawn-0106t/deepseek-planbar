"""Convert the DeepSeek whale logo (webp) to PNG for the WPF tray app.

Usage: PYTHONUTF8=1 python scripts/convert_logo.py
Input : deepseek.webp (repo root)
Output: tray-wpf/deepseek-logo.png (256x256, referenced as a WPF Resource)
"""
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "deepseek.webp"
DST = ROOT / "tray-wpf" / "deepseek-logo.png"


def main() -> None:
    if not SRC.exists():
        raise SystemExit(f"missing source logo: {SRC}")
    img = Image.open(SRC).convert("RGBA")
    if img.size != (256, 256):
        img = img.resize((256, 256), Image.LANCZOS)
    img.save(DST, format="PNG")
    print(f"saved: {DST} ({DST.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
