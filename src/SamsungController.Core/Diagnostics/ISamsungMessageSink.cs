using SamsungController.Core.Protocol;

namespace SamsungController.Core.Diagnostics;

public interface ISamsungMessageSink
{
    ValueTask WriteAsync(
        SamsungMessage message,
        CancellationToken cancellationToken = default);
}
