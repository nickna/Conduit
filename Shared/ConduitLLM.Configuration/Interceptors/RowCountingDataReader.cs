using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.Logging;

namespace ConduitLLM.Configuration.Interceptors;

/// <summary>
/// Wrapper around DbDataReader that counts rows as they are read.
/// Logs a warning when the row count exceeds the configured threshold.
/// </summary>
public class RowCountingDataReader : DbDataReader
{
    private readonly DbDataReader _innerReader;
    private readonly ILogger _logger;
    private readonly QueryMonitoringOptions _options;
    private readonly string _commandSummary;
    private int _rowCount;
    private bool _warningLogged;

    /// <summary>
    /// Creates a new instance of the RowCountingDataReader.
    /// </summary>
    /// <param name="innerReader">The underlying data reader</param>
    /// <param name="logger">The logger instance</param>
    /// <param name="options">The monitoring options</param>
    /// <param name="commandSummary">Summary of the command for logging</param>
    public RowCountingDataReader(
        DbDataReader innerReader,
        ILogger logger,
        QueryMonitoringOptions options,
        string commandSummary)
    {
        _innerReader = innerReader ?? throw new ArgumentNullException(nameof(innerReader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _commandSummary = commandSummary;
    }

    /// <inheritdoc/>
    public override bool Read()
    {
        var result = _innerReader.Read();
        if (result)
        {
            _rowCount++;
            CheckThreshold();
        }
        return result;
    }

    /// <inheritdoc/>
    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        var result = await _innerReader.ReadAsync(cancellationToken);
        if (result)
        {
            _rowCount++;
            CheckThreshold();
        }
        return result;
    }

    private void CheckThreshold()
    {
        if (!_warningLogged && _rowCount == _options.LargeResultSetThreshold)
        {
            _warningLogged = true;
            _logger.LogWarning(
                "Large result set detected ({RowCount}+ rows, threshold: {ThresholdRows}). " +
                "Consider using pagination. Command: {CommandSummary}",
                _rowCount,
                _options.LargeResultSetThreshold,
                _commandSummary);
        }
    }

    // Delegate all other properties and methods to the inner reader

    /// <inheritdoc/>
    public override int Depth => _innerReader.Depth;

    /// <inheritdoc/>
    public override int FieldCount => _innerReader.FieldCount;

    /// <inheritdoc/>
    public override bool HasRows => _innerReader.HasRows;

    /// <inheritdoc/>
    public override bool IsClosed => _innerReader.IsClosed;

    /// <inheritdoc/>
    public override int RecordsAffected => _innerReader.RecordsAffected;

    /// <inheritdoc/>
    public override object this[int ordinal] => _innerReader[ordinal];

    /// <inheritdoc/>
    public override object this[string name] => _innerReader[name];

    /// <inheritdoc/>
    public override bool GetBoolean(int ordinal) => _innerReader.GetBoolean(ordinal);

    /// <inheritdoc/>
    public override byte GetByte(int ordinal) => _innerReader.GetByte(ordinal);

    /// <inheritdoc/>
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
        => _innerReader.GetBytes(ordinal, dataOffset, buffer, bufferOffset, length);

    /// <inheritdoc/>
    public override char GetChar(int ordinal) => _innerReader.GetChar(ordinal);

    /// <inheritdoc/>
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
        => _innerReader.GetChars(ordinal, dataOffset, buffer, bufferOffset, length);

    /// <inheritdoc/>
    public override string GetDataTypeName(int ordinal) => _innerReader.GetDataTypeName(ordinal);

    /// <inheritdoc/>
    public override DateTime GetDateTime(int ordinal) => _innerReader.GetDateTime(ordinal);

    /// <inheritdoc/>
    public override decimal GetDecimal(int ordinal) => _innerReader.GetDecimal(ordinal);

    /// <inheritdoc/>
    public override double GetDouble(int ordinal) => _innerReader.GetDouble(ordinal);

    /// <inheritdoc/>
    [return: DynamicallyAccessedMembers(
        DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.PublicProperties)]
    public override Type GetFieldType(int ordinal) => _innerReader.GetFieldType(ordinal);

    /// <inheritdoc/>
    public override float GetFloat(int ordinal) => _innerReader.GetFloat(ordinal);

    /// <inheritdoc/>
    public override Guid GetGuid(int ordinal) => _innerReader.GetGuid(ordinal);

    /// <inheritdoc/>
    public override short GetInt16(int ordinal) => _innerReader.GetInt16(ordinal);

    /// <inheritdoc/>
    public override int GetInt32(int ordinal) => _innerReader.GetInt32(ordinal);

    /// <inheritdoc/>
    public override long GetInt64(int ordinal) => _innerReader.GetInt64(ordinal);

    /// <inheritdoc/>
    public override string GetName(int ordinal) => _innerReader.GetName(ordinal);

    /// <inheritdoc/>
    public override int GetOrdinal(string name) => _innerReader.GetOrdinal(name);

    /// <inheritdoc/>
    public override string GetString(int ordinal) => _innerReader.GetString(ordinal);

    /// <inheritdoc/>
    public override object GetValue(int ordinal) => _innerReader.GetValue(ordinal);

    /// <inheritdoc/>
    public override int GetValues(object[] values) => _innerReader.GetValues(values);

    /// <inheritdoc/>
    public override bool IsDBNull(int ordinal) => _innerReader.IsDBNull(ordinal);

    /// <inheritdoc/>
    public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken)
        => _innerReader.IsDBNullAsync(ordinal, cancellationToken);

    /// <inheritdoc/>
    public override bool NextResult() => _innerReader.NextResult();

    /// <inheritdoc/>
    public override Task<bool> NextResultAsync(CancellationToken cancellationToken)
        => _innerReader.NextResultAsync(cancellationToken);

    /// <inheritdoc/>
    public override IEnumerator GetEnumerator() => _innerReader.GetEnumerator();

    /// <inheritdoc/>
    public override DataTable? GetSchemaTable() => _innerReader.GetSchemaTable();

    /// <inheritdoc/>
    public override void Close() => _innerReader.Close();

    /// <inheritdoc/>
    public override Task CloseAsync() => _innerReader.CloseAsync();

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _innerReader.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <inheritdoc/>
    public override async ValueTask DisposeAsync()
    {
        await _innerReader.DisposeAsync();
        await base.DisposeAsync();
    }

    /// <inheritdoc/>
    public override T GetFieldValue<T>(int ordinal) => _innerReader.GetFieldValue<T>(ordinal);

    /// <inheritdoc/>
    public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken)
        => _innerReader.GetFieldValueAsync<T>(ordinal, cancellationToken);

    /// <inheritdoc/>
    public override Stream GetStream(int ordinal) => _innerReader.GetStream(ordinal);

    /// <inheritdoc/>
    public override TextReader GetTextReader(int ordinal) => _innerReader.GetTextReader(ordinal);
}
