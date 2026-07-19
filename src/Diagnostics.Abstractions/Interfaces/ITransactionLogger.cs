namespace Diagnostics.Abstractions.Interfaces;

/// <summary>
/// Single entry point for operation telemetry. There is no separate functional-area helper — <see cref="BeginTransaction"/> sets the category, and any <c>ILogger</c> line log written while the returned scope is open inherits its transaction id and category (see design doc §6).
/// </summary>
public interface ITransactionLogger
{
    /// <summary>
    /// Opens a new transaction scope. If called while another scope is already open (on the same async flow), the new scope becomes a child span (<c>ParentId</c> = the enclosing scope's <c>Id</c>).
    /// </summary>
    /// <param name="category">Functional area, e.g. "UploadPdf", "CropPdf", "OcrRun".</param>
    /// <param name="message">Optional operation summary (maps to <c>sMessage</c>).</param>
    ITransactionScope BeginTransaction(string category, string? message = null);
}
