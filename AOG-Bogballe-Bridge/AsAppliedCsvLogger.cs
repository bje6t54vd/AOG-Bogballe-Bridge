using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AOGBogballeBridge
{
    internal sealed class AsAppliedCsvLogger : IAsyncDisposable
    {
        private const int QueueCapacity = 1024;
        private const int CsvFieldCount = 33;
        private const string MetadataNote = "Operational data only; not a certified or Danish statutory as-applied record. Requested target and section targets are bridge commands; calibrator rate is a set-value readback, not measured physical application.";
        private readonly Channel<string> _rows;
        private readonly StreamWriter _writer;
        private readonly Task _writerTask;
        private string? _failureMessage;

        public string FilePath { get; }
        public string? FailureMessage => Volatile.Read(ref _failureMessage);

        public AsAppliedCsvLogger(string filePath)
        {
            FilePath = filePath;
            _writer = new StreamWriter(
                new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };
            _rows = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });

            try
            {
                _writer.WriteLine("record_type,timestamp_utc,gps_latitude_deg,gps_longitude_deg,toolbar_latitude_deg,toolbar_longitude_deg,heading_deg,machine_width_m,speed_kmh,section_count,requested_target_rate_kg_ha,calibrator_set_rate_readback_kg_ha,calibrator_readback_utc,calibrator_readback_age_seconds,source_shapefile,source_field,section_1_on,section_2_on,section_3_on,section_4_on,section_5_on,section_6_on,section_7_on,section_8_on,section_1_target_rate_kg_ha,section_2_target_rate_kg_ha,section_3_target_rate_kg_ha,section_4_target_rate_kg_ha,section_5_target_rate_kg_ha,section_6_target_rate_kg_ha,section_7_target_rate_kg_ha,section_8_target_rate_kg_ha,note");
                _writer.WriteLine($"metadata{new string(',', CsvFieldCount - 1)}\"{MetadataNote}\"");
            }
            catch (IOException)
            {
                _writer.Dispose();
                throw;
            }
            catch (UnauthorizedAccessException)
            {
                _writer.Dispose();
                throw;
            }

            _writerTask = WriteRowsAsync();
        }

        public bool TryWrite(string row)
        {
            if (_writerTask.IsCompleted)
                return false;

            if (_rows.Writer.TryWrite(row))
                return true;

            Volatile.Write(ref _failureMessage, "The recording queue filled; some samples may not have been written.");
            return false;
        }

        public async ValueTask DisposeAsync()
        {
            _rows.Writer.TryComplete();
            await _writerTask.ConfigureAwait(false);
        }

        private async Task WriteRowsAsync()
        {
            try
            {
                await foreach (string row in _rows.Reader.ReadAllAsync().ConfigureAwait(false))
                    await _writer.WriteLineAsync(row).ConfigureAwait(false);
                await _writer.FlushAsync().ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                SetWriterFailure(ex);
                throw;
            }
            catch (UnauthorizedAccessException ex)
            {
                SetWriterFailure(ex);
                throw;
            }
            catch (ObjectDisposedException ex)
            {
                SetWriterFailure(ex);
                throw;
            }
            finally
            {
                await _writer.DisposeAsync().ConfigureAwait(false);
            }
        }

        private void SetWriterFailure(Exception exception)
        {
            Volatile.Write(ref _failureMessage, exception.Message);
            _rows.Writer.TryComplete(exception);
        }
    }
}
