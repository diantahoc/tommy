# Tommy PDF page renderer — rasterizes each page to a PNG via PyMuPDF.
# The C# side (FilePaneViewModel) orchestrates and displays the images.
# Invoked isolated: `python -I pdf_render.py <path-to-pdf> <out-dir> [dpi]`.
# Prints one output PNG path per line, in page order.
import sys

try:
    import fitz  # PyMuPDF
except Exception:
    sys.stderr.write("PyMuPDF is not installed. Run: pip install PyMuPDF")
    sys.exit(3)

if len(sys.argv) < 3:
    sys.stderr.write("usage: pdf_render.py <path-to-pdf> <out-dir> [dpi]")
    sys.exit(2)

path = sys.argv[1]
out_dir = sys.argv[2]
dpi = int(sys.argv[3]) if len(sys.argv) > 3 else 130

doc = fitz.open(path)
for i, page in enumerate(doc):
    pix = page.get_pixmap(dpi=dpi)
    out = f"{out_dir}/page-{i:04d}.png"
    pix.save(out)
    print(out)
