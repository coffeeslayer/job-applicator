from reportlab.lib import colors
from reportlab.lib.enums import TA_LEFT
from reportlab.lib.pagesizes import letter
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.units import inch
from reportlab.pdfgen import canvas
from reportlab.platypus import Paragraph

out = r"C:\Users\18502\Documents\Repos\Job Applicator\templates\Resume.pdf"
page_w, page_h = letter
margin = 0.62 * inch
content_w = page_w - 2 * margin
c = canvas.Canvas(out, pagesize=letter)
c.setTitle("Resume Template")
c.setAuthor("Job Applicator project template")
navy = colors.HexColor("#18324B")
blue = colors.HexColor("#2A6F97")
muted = colors.HexColor("#536574")
light = colors.HexColor("#E8EEF2")
pale = colors.HexColor("#F5F8FA")

body = ParagraphStyle("body", fontName="Helvetica", fontSize=9, leading=12, textColor=muted)
small = ParagraphStyle("small", fontName="Helvetica", fontSize=8.2, leading=11, textColor=muted)
summary = ParagraphStyle("summary", fontName="Helvetica-Oblique", fontSize=9, leading=12.5, textColor=muted)

# Header
c.setFillColor(navy)
c.rect(0, page_h - 1.55 * inch, page_w, 1.55 * inch, stroke=0, fill=1)
c.setFillColor(colors.HexColor("#A9C8DA"))
c.setFont("Helvetica-Bold", 8)
c.drawString(margin, page_h - 0.36 * inch, "RESUME TEMPLATE / STARTER LAYOUT")
c.setFillColor(colors.white)
c.setFont("Helvetica-Bold", 25)
c.drawString(margin, page_h - 0.79 * inch, "[FULL NAME]")
c.setFont("Helvetica", 8.8)
c.drawString(margin, page_h - 1.08 * inch, "[City, State]  |  [Email]  |  [Phone]")
c.drawString(margin, page_h - 1.29 * inch, "[LinkedIn URL]  |  [Website or portfolio]")

y = page_h - 1.83 * inch

def section(title):
    global y
    c.setFillColor(navy)
    c.setFont("Helvetica-Bold", 9.3)
    c.drawString(margin, y, title.upper())
    c.setStrokeColor(light)
    c.setLineWidth(0.7)
    c.line(margin, y - 4, page_w - margin, y - 4)
    y -= 20

def para(text, style=body, gap=4):
    global y
    p = Paragraph(text, style)
    w, h = p.wrap(content_w, page_h)
    p.drawOn(c, margin, y - h)
    y -= h + gap

def role(title, employer, dates):
    global y
    c.setFillColor(navy)
    c.setFont("Helvetica-Bold", 8.8)
    c.drawString(margin, y, f"{title} - {employer}")
    c.setFillColor(muted)
    c.setFont("Helvetica", 7.9)
    c.drawRightString(page_w - margin, y, dates)
    y -= 12

section("Professional summary")
para("[Write 2-4 concise sentences about your experience, strengths, and target roles. Focus on the value you bring to the type of role you want.]", summary, 9)

section("Core skills")
para("[Skill 1]  |  [Skill 2]  |  [Skill 3]  |  [Skill 4]  |  [Tools and technologies you can verify]", body, 9)

section("Experience")
role("[Job title]", "[Employer]", "[Month YYYY - Month YYYY]")
para("- [Describe a relevant responsibility, project, or result. Include scale or measurable impact when verified.]", small, 3)
para("- [Add a second contribution using a clear action and outcome.]", small, 3)
para("- [Add a third accomplishment or technical contribution.]", small, 8)
role("[Job title]", "[Employer]", "[Month YYYY - Month YYYY]")
para("- [Describe a relevant responsibility or result.]", small, 3)
para("- [Add another specific, verified contribution.]", small, 3)
para("- [Add a third accomplishment, project, or area of ownership.]", small, 8)

section("Education")
c.setFillColor(navy)
c.setFont("Helvetica-Bold", 8.7)
c.drawString(margin, y, "[Degree] - [School]")
c.setFillColor(muted)
c.setFont("Helvetica", 7.8)
c.drawRightString(page_w - margin, y, "[Graduation year]")
y -= 12
para("[City, State] | [Graduation year or attendance dates]", small, 3)
para("[Optional honors, relevant coursework, or academic projects]", small, 8)

section("Certifications")
para("[Certification - Issuer - Year]  |  [Certification - Issuer - Year]", small, 8)

section("Selected project")
para("[Project name - purpose, tools, your contribution, and a verified outcome or link.]", small, 8)

# Small template-use note
note_h = 30
c.setFillColor(pale)
c.roundRect(margin, 0.46 * inch, content_w, note_h, 5, stroke=0, fill=1)
p = Paragraph("Template only: replace every bracketed item with verified information and remove sections that do not apply. Edit the source document in a word processor, then export a completed copy as PDF.", small)
w, h = p.wrap(content_w - 18, note_h - 6)
p.drawOn(c, margin + 9, 0.46 * inch + (note_h - h) / 2)

c.showPage()
c.save()
