/* In the name of God, the Merciful, the Compassionate */

namespace SQLTriage.Data.Models;

/// <summary>Settings returned when the user confirms a PDF export via PdfExportModal.</summary>
public class PdfExportSettings
{
    public string FileName { get; set; } = "Export.pdf";
    public bool LandscapeOrientation { get; set; } = true;
    public bool PrintBackgrounds { get; set; } = true;
}
