"""Render the repository's user guide to a PDF. Requires reportlab and Pillow."""
from pathlib import Path
from html import escape
import argparse
import re

from PIL import Image as PILImage
from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.lib.units import mm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, Table, TableStyle, Image, PageBreak, Preformatted

def render(source, output, version):
    fonts = Path("C:/Windows/Fonts")
    for name, filename in [("CS", "segoeui.ttf"), ("CSBold", "segoeuib.ttf"), ("CSMono", "consola.ttf")]:
        if not (fonts / filename).is_file():
            raise RuntimeError("Windows Segoe UI/Consolas fonts are required to render this guide.")
        pdfmetrics.registerFont(TTFont(name, str(fonts / filename)))
    pdfmetrics.registerFontFamily("CS", normal="CS", bold="CSBold", italic="CS", boldItalic="CSBold")
    green = colors.HexColor("#1B5E20")
    styles = getSampleStyleSheet()
    styles.add(ParagraphStyle("CSBody", fontName="CS", fontSize=10.5, leading=15, spaceAfter=7, textColor=colors.HexColor("#243528")))
    styles.add(ParagraphStyle("CSTitle", parent=styles["CSBody"], fontName="CSBold", fontSize=23, leading=29, spaceAfter=16, textColor=green))
    styles.add(ParagraphStyle("CSHeading", parent=styles["CSBody"], fontName="CSBold", fontSize=16, leading=21, spaceAfter=12, textColor=green))
    styles.add(ParagraphStyle("CSSub", parent=styles["CSBody"], fontName="CSBold", fontSize=11.5, leading=16, spaceBefore=6))
    styles.add(ParagraphStyle("CSCaption", parent=styles["CSBody"], fontSize=9, leading=12, alignment=TA_CENTER, textColor=colors.HexColor("#506255"), spaceAfter=12))
    styles.add(ParagraphStyle("CSCell", parent=styles["CSBody"], fontSize=9.5, leading=13, spaceAfter=0))
    width = A4[0] - 36 * mm
    def inline(value):
        value = escape(value)
        value = re.sub(r"\*\*(.+?)\*\*", r"<b>\1</b>", value)
        value = re.sub(r"&#x27;", "'", value)
        value = re.sub(r"\[([^\]]+)\]\((https?://[^)]+)\)", r'<link href="\2" color="#1B5E20"><u>\1</u></link>', value)
        value = re.sub(r"\[([^\]]+)\]\([^)]+\)", r"\1", value)
        value = re.sub(r"\x60([^\x60]+)\x60", r'<font name="CSMono" size="9">\1</font>', value)
        return value
    lines = source.read_text(encoding="utf-8-sig").replace("Versi 1.0.0", "Versi " + version).splitlines()
    story = []
    i = 0
    while i < len(lines):
        line = lines[i].strip()
        if not line:
            i += 1
            continue
        if line == "<!-- pagebreak -->":
            story.append(PageBreak())
            i += 1
            continue
        if line.startswith("#"):
            level = len(line) - len(line.lstrip("#"))
            story.append(Paragraph(inline(line[level:].strip()), styles["CSTitle" if level == 1 else "CSHeading" if level == 2 else "CSSub"]))
        elif line.startswith("!["):
            match = re.fullmatch(r"!\[(.*?)\]\((.*?)\)", line)
            if not match:
                raise ValueError("Malformed image line: " + line)
            path = source.parent / match.group(2)
            with PILImage.open(path) as picture:
                w, h = picture.size
            max_height = (120 if path.name == "01-database.png" else 140) * mm
            scale = min(width / w, max_height / h)
            story.append(Image(str(path), width=w * scale, height=h * scale))
            story.append(Spacer(1, 5))
            story.append(Paragraph(inline(match.group(1)), styles["CSCaption"]))
        elif line.startswith("|"):
            rows = []
            while i < len(lines) and lines[i].strip().startswith("|"):
                parts = [p.strip() for p in lines[i].strip().strip("|").split("|")]
                if not all(re.fullmatch(r":?-+:?", p) for p in parts):
                    rows.append([Paragraph(inline(p), styles["CSCell"]) for p in parts])
                i += 1
            widths = [width * .31, width * .69] if len(rows[0]) == 2 else [width / len(rows[0])] * len(rows[0])
            table = Table(rows, colWidths=widths, repeatRows=1, hAlign="LEFT")
            table.setStyle(TableStyle([
                ("BACKGROUND", (0, 0), (-1, 0), colors.HexColor("#E7F0E7")),
                ("VALIGN", (0, 0), (-1, -1), "TOP"), ("BOX", (0, 0), (-1, -1), .5, colors.HexColor("#C7D5C7")),
                ("INNERGRID", (0, 0), (-1, -1), .25, colors.HexColor("#D7E0D7")),
                ("LEFTPADDING", (0, 0), (-1, -1), 8), ("RIGHTPADDING", (0, 0), (-1, -1), 8),
                ("TOPPADDING", (0, 0), (-1, -1), 6), ("BOTTOMPADDING", (0, 0), (-1, -1), 6)]))
            story.extend([table, Spacer(1, 10)])
            continue
        elif line.startswith("\x60\x60\x60"):
            block = []
            i += 1
            while i < len(lines) and not lines[i].startswith("\x60\x60\x60"):
                block.append(lines[i])
                i += 1
            code_style = ParagraphStyle("CSCode", fontName="CSMono", fontSize=8, leading=11, spaceAfter=10)
            story.append(Preformatted("\n".join(block), code_style, maxLineLength=95))
        elif line.startswith("- ") or re.match(r"^\d+\. ", line):
            story.append(Paragraph(inline(line), styles["CSBody"]))
        else:
            paragraph = [line]
            while i + 1 < len(lines) and lines[i + 1].strip() and not re.match(r"^(#|!|\||<!--|-\s|\d+\.\s|\x60\x60\x60)", lines[i + 1]):
                i += 1
                paragraph.append(lines[i].strip())
            story.append(Paragraph(inline(" ".join(paragraph)), styles["CSBody"]))
        i += 1

    output.parent.mkdir(parents=True, exist_ok=True)
    def footer(canvas, doc):
        canvas.saveState()
        canvas.setStrokeColor(colors.HexColor("#C7D5C7"))
        canvas.line(18 * mm, 16 * mm, A4[0] - 18 * mm, 16 * mm)
        canvas.setFont("CS", 8)
        canvas.setFillColor(colors.HexColor("#506255"))
        canvas.drawString(18 * mm, 11 * mm, "CarbonShift | Panduan Pengguna | " + version)
        canvas.drawRightString(A4[0] - 18 * mm, 11 * mm, str(doc.page))
        canvas.restoreState()
    doc = SimpleDocTemplate(str(output), pagesize=A4, leftMargin=18 * mm, rightMargin=18 * mm,
        topMargin=17 * mm, bottomMargin=23 * mm, title="Panduan Pengguna CarbonShift", author="Kelompok CarbonShift")
    doc.build(story, onFirstPage=footer, onLaterPages=footer)

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--version", default="1.0.0")
    args = parser.parse_args()
    render(args.source.resolve(), args.output.resolve(), args.version)
    print("PDF generated: " + str(args.output.resolve()))

