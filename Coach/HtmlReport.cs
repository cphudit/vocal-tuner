using System.Net;
using System.Text;
using System.Windows;

namespace VocalTuner.Coach;

/// <summary>Self-contained HTML report (graphs embedded as base64 PNG, light theme, prints cleanly to PDF).</summary>
public static class HtmlReport
{
    static string E(string s) => WebUtility.HtmlEncode(s);

    public static string Build(CoachResult r, CoachResult? prev, string lang)
    {
        string L(string th, string en) => Feedback.L(lang, th, en);
        var p = Theme.Light;
        string Img(double w, double h, Action<System.Windows.Media.DrawingContext> draw) =>
            "data:image/png;base64," + Convert.ToBase64String(Charts.Png(w, h, draw, p.Surface));

        var sb = new StringBuilder();
        sb.Append($$"""
<!doctype html>
<html lang="{{(lang == "th" ? "th" : "en")}}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{E(L("รายงานการร้อง", "Singing report"))}} · {{E(Feedback.SourceName(r.Source, lang))}}</title>
<style>
  :root { --bg:#faf9f7; --card:#fff; --text:#1f1e1c; --sub:#6f6c66; --faint:#9a978f; --line:#e5e2db;
          --green:#3f8a5d; --amber:#a5801f; --red:#b04a3e; }
  * { box-sizing: border-box; }
  body { margin:0; background:var(--bg); color:var(--text);
         font: 15px/1.6 "Segoe UI", "Leelawadee UI", "Noto Sans Thai", "Sarabun", Tahoma, sans-serif; }
  main { max-width: 900px; margin: 0 auto; padding: 32px 16px 60px; }
  h1 { font-size: 24px; margin: 0 0 4px; }
  h2 { font-size: 17px; margin: 34px 0 10px; }
  .meta { color: var(--sub); font-size: 13px; }
  .card { background: var(--card); border: 1px solid var(--line); border-radius: 12px; padding: 16px 18px; }
  .summary { font-size: 16px; }
  .warn { border-color: #e2c48a; background: #fdf7ea; margin-top: 14px; }
  table { border-collapse: collapse; width: 100%; font-size: 14px; }
  td { padding: 6px 4px; border-bottom: 1px solid var(--line); vertical-align: top; }
  td:last-child { text-align: right; font-variant-numeric: tabular-nums; }
  .item { margin: 0 0 12px; }
  .item h3 { font-size: 15px; margin: 0 0 4px; }
  .item p { margin: 2px 0; }
  .k { color: var(--sub); }
  ul { margin: 4px 0 0; padding-left: 20px; }
  .good h3::before { content: "✓ "; color: var(--green); }
  .rank { display:inline-block; min-width: 22px; color: var(--sub); }
  img { width: 100%; height: auto; display: block; }
  .chart { margin: 8px 0 14px; }
  .legend { font-size: 13px; color: var(--sub); }
  .sw { display:inline-block; width:10px; height:10px; border-radius:2px; margin: 0 4px 0 10px; vertical-align: -1px; }
  .up { color: var(--green); } .down { color: var(--red); }
  @media print { body { background: #fff; } .card { break-inside: avoid; } h2 { break-after: avoid; } }
</style>
</head>
<body><main>
""");
        sb.Append($"<h1>{E(L("รายงานการร้องเพลง", "Singing report"))}</h1>");
        sb.Append($"<div class=meta>{E(Feedback.SourceName(r.Source, lang))} · {E(Feedback.Time(r.Duration))} · {r.When:yyyy-MM-dd HH:mm} · Vocal Tuner</div>");

        sb.Append($"<h2>{E(L("สรุป", "Summary"))}</h2><div class='card summary'>{E(Feedback.Summary(r, lang))}</div>");
        if (r.Caveats.Contains("music") || r.Caveats.Contains("bleed"))
            sb.Append($"<div class='card warn'>{E(Feedback.CaveatText(r, lang)[0])}</div>");

        sb.Append($"<h2>{E(L("จุดแข็ง", "Strengths"))}</h2><div class=card>");
        foreach (var f in r.Strengths)
        {
            var (t, d) = Feedback.Strength(r, f, lang);
            sb.Append($"<div class='item good'><h3>{E(t)}</h3><p class=k>{E(d)}</p></div>");
        }
        sb.Append("</div>");

        sb.Append($"<h2>{E(L("จุดที่ควรแก้ (เรียงตามความสำคัญ)", "Weaknesses (most important first)"))}</h2><div class=card>");
        if (r.Weaknesses.Count == 0) sb.Append($"<p>{E(L("ไม่เจอปัญหาใหญ่ในเทคนี้", "No major problems found in this take."))}</p>");
        int k = 1;
        foreach (var f in r.Weaknesses)
        {
            sb.Append($"<div class=item><h3><span class=rank>{k++}.</span>{E(Feedback.Title(r, f, lang))}</h3>");
            sb.Append($"<p><b>{E(L("วัดได้", "Measured"))}:</b> {E(Feedback.Measured(r, f, lang))}</p>");
            var where = Feedback.Where(r, f, lang);
            if (where != "") sb.Append($"<p><b>{E(L("ตรงไหน", "Where"))}:</b> {E(where)}</p>");
            sb.Append($"<p><b>{E(L("สาเหตุที่น่าจะเป็น", "Likely cause"))}:</b> {E(Feedback.Cause(r, f, lang))}</p>");
            sb.Append("</div>");
        }
        sb.Append("</div>");

        if (r.Weaknesses.Any(f => Feedback.Exercises(r, f, lang).Count > 0))
        {
            sb.Append($"<h2>{E(L("สิ่งที่ควรฝึก", "What to work on"))}</h2><div class=card>");
            k = 1;
            foreach (var f in r.Weaknesses)
            {
                int n = k++;
                var ex = Feedback.Exercises(r, f, lang);
                if (ex.Count == 0) continue;
                sb.Append($"<div class=item><h3><span class=rank>{n}.</span>{E(Feedback.Title(r, f, lang))}</h3><ul>"
                    + string.Concat(ex.Select(x => $"<li>{E(x)}</li>")) + "</ul></div>");
            }
            sb.Append("</div>");
        }

        var plan = Feedback.Plan(r, lang);
        sb.Append($"<h2>{E(L("แผนฝึกวันละ", "Daily practice plan:"))} {plan.Sum(x => x.min)} {E(L("นาที", "minutes"))}</h2><div class=card><ul>");
        foreach (var (min, text) in plan) sb.Append($"<li><b>{min} {E(L("นาที", "min"))}</b> · {E(text)}</li>");
        sb.Append("</ul></div>");

        sb.Append($"<h2>{E(L("เสียงของคุณบอกอะไร", "What this says about your voice"))}</h2><div class=card><ul>");
        foreach (var l in Feedback.Voice(r, lang)) sb.Append($"<li>{E(l)}</li>");
        sb.Append("</ul></div>");

        if (prev != null)
        {
            sb.Append($"<h2>{E(L("พัฒนาการเทียบกับครั้งก่อน", "Progress since last time"))}</h2><div class=card>");
            sb.Append($"<p class=k>{E(L("เทียบกับ", "Compared with"))} {E(Feedback.SourceName(prev.Source, lang))} · {prev.When:yyyy-MM-dd HH:mm}</p><table>");
            foreach (var d in Feedback.Compare(r, prev, lang))
            {
                string cls = d.Better > 0 ? "up" : d.Better < 0 ? "down" : "";
                string mark = d.Better > 0 ? L("ดีขึ้น", "better") : d.Better < 0 ? L("แย่ลง", "worse") : L("เท่าเดิม", "same");
                sb.Append($"<tr><td>{E(d.Label)}</td><td>{E(d.Before)} → {E(d.After)} <span class={cls}>{E(mark)}</span></td></tr>");
            }
            sb.Append("</table></div>");
        }

        sb.Append($"<h2>{E(L("กราฟ", "Graphs"))}</h2>");
        sb.Append($"<div class=card><b>{E(L("เสียงร้องตามเวลา", "Your pitch over time"))}</b>");
        sb.Append($"<div class=legend>{E(L("กล่อง = โน้ตที่ร้อง สีบอกความตรง", "Boxes = sung notes, coloured by accuracy"))}"
            + $"<span class=sw style='background:var(--green)'></span>±20¢<span class=sw style='background:var(--amber)'></span>±35¢<span class=sw style='background:var(--red)'></span>{E(L("เกินกว่านั้น", "more"))}"
            + $" · {E(L("กล่องมีขอบ = มีปัญหา (ช้อน ตก ส่าย นอกคีย์)", "outlined = flagged (scoop, falling end, wander, out of key)"))} · ▼ {E(L("เสียงแตก", "crack"))}</div>");
        double rowH = Charts.PitchRowHeight(r);
        for (int row = 0; row < Charts.PitchRows(r); row++)
        {
            int rr = row;
            sb.Append($"<div class=chart><img alt='{E(L("กราฟ pitch ช่วง", "Pitch graph from"))} {Feedback.Time(row * Charts.RowSec)}' src='{Img(860, rowH, dc => Charts.PitchRow(dc, new Size(860, rowH), r, rr, p, 1))}'></div>");
        }
        sb.Append("</div>");
        sb.Append($"<div class=card style='margin-top:14px'><b>{E(L("ความคลาดของแต่ละโน้ต (เซนต์)", "How far each note was from its target (cents)"))}</b>"
            + $"<div class=chart><img alt='histogram' src='{Img(860, 190, dc => Charts.Histogram(dc, new Size(860, 190), r, p, 1, lang))}'></div></div>");
        sb.Append($"<div class=card style='margin-top:14px'><b>{E(L("ความดังตามเวลา (แถบเทา = ประโยค)", "Loudness over time (grey bands = phrases)"))}</b>"
            + $"<div class=chart><img alt='loudness' src='{Img(860, 160, dc => Charts.Loudness(dc, new Size(860, 160), r, p, 1))}'></div></div>");
        sb.Append($"<div class=card style='margin-top:14px'><b>{E(L("คุณภาพเสียงแต่ละช่วง", "Voice quality by pitch zone"))}</b>"
            + $"<div class=chart><img alt='zones' src='{Img(860, 200, dc => Charts.Zones(dc, new Size(860, 200), r, p, 1, lang))}'></div>"
            + $"<div class=legend>{E(L("แท่งจาง = มีโน้ตน้อยกว่า 5 ตัว", "Faded bars = fewer than 5 notes"))}</div></div>");

        sb.Append($"<h2>{E(L("ข้อจำกัด", "Limits"))}</h2><div class=card><ul>");
        foreach (var l in Feedback.CaveatText(r, lang)) sb.Append($"<li>{E(l)}</li>");
        sb.Append("</ul></div>");
        sb.Append($"<h2>{E(L("ตัวเลขสำคัญ", "Key numbers"))}</h2><div class=card><table>");
        foreach (var (label, value) in Feedback.Numbers(r, lang)) sb.Append($"<tr><td>{E(label)}</td><td>{E(value)}</td></tr>");
        sb.Append("</table></div>");

        sb.Append("</main></body></html>");
        return sb.ToString();
    }
}
