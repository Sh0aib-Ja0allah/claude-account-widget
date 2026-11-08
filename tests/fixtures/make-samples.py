# Generates the file preview test samples (h2) into a scratch folder: make-samples.py <scratch p2 folder>
# Everything is invented sample text. It also writes page-samples\sample.pdf next to that folder for the compare test.
import os, sys
out=sys.argv[1]
from reportlab.pdfgen import canvas
c=canvas.Canvas(os.path.join(out,'report.pdf'))
for i in range(1,7):
    c.setFont('Helvetica-Bold',22); c.drawString(72,760,'Report page %d'%i)
    c.setFont('Helvetica',13)
    y=720
    for j in range(14):
        c.drawString(72,y,'This line of the report talks about quarterly sales in region %d.'%(j+1)); y-=22
    if i in (2,5):
        c.drawString(72,y-10,'The hidden needle phrase appears here.')
    c.setFillColorRGB(0.77,0.33,0.18); c.rect(72,120,180,70,fill=1); c.setFillColorRGB(0,0,0)
    c.showPage()
c.save()
from pptx import Presentation
p=Presentation()
for i in range(1,5):
    s=p.slides.add_slide(p.slide_layouts[1]); s.shapes.title.text='Slide title %d'%i; s.placeholders[1].text='Point one on slide %d\nPoint two'%i
p.save(os.path.join(out,'deck.pptx'))
from openpyxl import Workbook
from openpyxl.styles import Font, PatternFill
w=Workbook(); a=w.active; a.title='Sales'
a.append(['Item','Qty','Price'])
for c2 in a[1]:
    c2.font=Font(bold=True,color='FFFFFF'); c2.fill=PatternFill('solid',fgColor='C4552F')
for r in range(30): a.append(['Product %d'%r,r,r*1.5])
b=w.create_sheet('Costs'); b.append(['Cost','Amount']); b.append(['Rent',1000])
h=w.create_sheet('Secret'); h.append(['x']); h.sheet_state='hidden'
w.save(os.path.join(out,'book.xlsx'))
import zipfile
W='http://schemas.openxmlformats.org/wordprocessingml/2006/main'
doc='<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="%s"><w:body><w:p><w:pPr><w:jc w:val="center"/></w:pPr><w:r><w:rPr><w:b/><w:sz w:val="48"/></w:rPr><w:t>Word sample</w:t></w:r></w:p><w:p><w:r><w:t>A paragraph in a real Word document with a searchable term.</w:t></w:r></w:p><w:p><w:r><w:br w:type="page"/><w:t>Second page.</w:t></w:r></w:p></w:body></w:document>'%W
with zipfile.ZipFile(os.path.join(out,'letter.docx'),'w') as z:
    z.writestr('[Content_Types].xml','<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>')
    z.writestr('_rels/.rels','<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>')
    z.writestr('word/document.xml',doc)
open(os.path.join(out,'notes.md'),'w',encoding='utf-8').write('''---
title: x
---
# Notes heading

Some **bold** text, some *italic*, `inline code` and a [link](https://example.com).

## Lists

- first item
- second item
  - nested item
- third

1. one
2. two

> A quote that spans
> two lines.

| Name | Qty |
|------|-----|
| Apple | 3 |
| Pear | 5 |

```python
def hello(name):
    # greet someone
    return f"hello {name}" + str(42)
```

---

مرحبا بالعالم هذا نص عربي للتجربة

Final paragraph with needle inside.
''')
open(os.path.join(out,'page.html'),'w',encoding='utf-8').write('''<html><head><style>body{font-family:sans-serif;margin:40px}h1{color:#c4552f}.box{background:linear-gradient(90deg,#c4552f,#f5b041);height:80px;border-radius:12px}</style></head>
<body><h1 id="a">Static heading</h1><script>document.getElementById('a').textContent='SCRIPT RAN';fetch('http://example.com/x');</script><div class="box"></div><p>Paragraph with needle word on a web page.</p></body></html>''')
open(os.path.join(out,'arabic.html'),'w',encoding='utf-8').write('<html dir="rtl"><body style="font-family:Segoe UI;font-size:22px;margin:40px"><h1>تقرير المبيعات</h1><p>هذا نص عربي طويل عن المبيعات في المنطقة ويجب أن تتعرف عليه النافذة بشكل صحيح</p></body></html>')
open(os.path.join(out,'data.csv'),'w',encoding='utf-8').write('name,qty,note\n"Smith, John",3,"said ""hi"""\nPear,5,needle here\nShort\n'+''.join('Row %d,%d,x\n'%(i,i) for i in range(200)))
open(os.path.join(out,'tool.py'),'w',encoding='utf-8').write('# A sample script\nimport os\n\ndef main(path):\n    """Doc string"""\n    for i in range(3):\n        print("value", i, 0x1F)\n    return None\n'*6)
open(os.path.join(out,'pic.svg'),'w',encoding='utf-8').write('<svg xmlns="http://www.w3.org/2000/svg" width="400" height="200"><rect width="400" height="200" fill="#c4552f"/><text x="40" y="110" font-size="36" fill="white">SVG sample</text></svg>')
from PIL import Image, ImageDraw
im=Image.new('RGB',(2400,1400),(40,90,160)); dr=ImageDraw.Draw(im)
for i in range(0,2400,100): dr.line((i,0,i,1400),fill=(255,255,255))
im.save(os.path.join(out,'big.png'))
print('ok')

# The compare test's small PDF, generated instead of committed.
ps=os.path.join(os.path.dirname(os.path.abspath(out)),'page-samples')
os.makedirs(ps,exist_ok=True)
c2=canvas.Canvas(os.path.join(ps,'sample.pdf'))
for i in range(1,3):
    c2.setFont('Helvetica',14); c2.drawString(72,760,'Sample page %d'%i); c2.showPage()
c2.save()
