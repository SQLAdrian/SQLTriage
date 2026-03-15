/* In the name of God, the Merciful, the Compassionate */

using Microsoft.Reporting.NETCore;
using System.Data;
using System.IO;
using System.Collections.Generic;
using System.Reflection;

namespace SqlHealthAssessment.Data.Services;

/// <summary>
/// Service for generating reports from RDL files using ReportViewerCore.
/// </summary>
public class RdlReportService
{
    private readonly string _basePath;

    public RdlReportService()
    {
        // Get the base path from the executing assembly location
        _basePath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
    }

    /// <summary>
    /// Gets a list of available RDL report files.
    /// </summary>
    public List<string> GetAvailableReports()
    {
        var reportsDirectory = Path.Combine(_basePath, "Reports");
        if (!Directory.Exists(reportsDirectory))
            return new List<string>();

        return Directory.GetFiles(reportsDirectory, "*.rdl")
            .Select(f => Path.GetFileName(f))
            .ToList();
    }

    /// <summary>
    /// Generates a PDF report from an RDL file using the provided data source.
    /// </summary>
    /// <param name="reportFileName">Name of the RDL file (e.g., "BlitzReport.rdl")</param>
    /// <param name="dataSourceName">Name of the data source in the RDL</param>
    /// <param name="data">DataTable to use as the data source</param>
    /// <param name="parameters">Optional report parameters</param>
    /// <returns>PDF file bytes</returns>
    public byte[] GeneratePdfReport(string reportFileName, string dataSourceName, DataTable data, Dictionary<string, string>? parameters = null)
    {
        var dataSources = new Dictionary<string, DataTable> { { dataSourceName, data } };
        return GeneratePdfReport(reportFileName, dataSources, parameters);
    }

    /// <summary>
    /// Generates a PDF report from an RDL file using multiple data sources.
    /// </summary>
    /// <param name="reportFileName">Name of the RDL file (e.g., "BlitzReport.rdl")</param>
    /// <param name="dataSources">Dictionary of data source names to DataTables</param>
    /// <param name="parameters">Optional report parameters</param>
    /// <returns>PDF file bytes</returns>
    public byte[] GeneratePdfReport(string reportFileName, Dictionary<string, DataTable> dataSources, Dictionary<string, string>? parameters = null)
    {
        var reportPath = Path.Combine(_basePath, "Reports", reportFileName);
        
        if (!File.Exists(reportPath))
            throw new FileNotFoundException($"Report file not found: {reportPath}");

        var localReport = new LocalReport();
        localReport.ReportPath = reportPath;
        
        // Add data sources
        foreach (var ds in dataSources)
        {
            localReport.DataSources.Add(new ReportDataSource(ds.Key, ds.Value));
        }
        
        // Render to PDF
        var result = localReport.Render(
            "PDF",
            null,
            out string mimeType,
            out string encoding,
            out string fileNameExtension,
            out string[] streams,
            out Warning[] warnings);

        return result;
    }

    /// <summary>
    /// Gets the output directory for storing generated reports.
    /// </summary>
    public string GetOutputDirectory()
    {
        var outputDir = Path.Combine(_basePath, "Output");
        if (!Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);
        return outputDir;
    }
}
