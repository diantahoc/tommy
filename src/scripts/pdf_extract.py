# Tommy PDF text extractor — page-by-page text via PyMuPDF.
# The C# side (PdfExtractTool) orchestrates; Python does the work.
# Invoked isolated: `python -I pdf_extract.py <path-to-pdf>` so a module
# planted next to the PDF can't be imported in place of a real dependency.
import sys

try:
    import fitz  # PyMuPDF
except Exception:
    sys.stderr.write("PyMuPDF is not installed. Run: pip install PyMuPDF")
    sys.exit(3)

if len(sys.argv) < 2:
    sys.stderr.write("usage: pdf_extract.py <path-to-pdf>")
    sys.exit(2)

path = sys.argv[1]
doc = fitz.open(path)
out = []
for i, page in enumerate(doc, start=1):
    out.append(f"--- page {i} ---\n{page.get_text()}".rstrip())
print("\n\n".join(out))
