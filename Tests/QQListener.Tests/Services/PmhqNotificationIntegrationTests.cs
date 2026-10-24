using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using QQListener.Core;
using QQListener.Models;
using QQListener.Protocol;
using QQListener.Services;
using Xunit;

namespace QQListener.Tests.Services;

public sealed class PmhqNotificationIntegrationTests
{
    [Fact]
    public async Task PmhqPacketUsesTheSameRulesPendingDigestAndClassIslandRequest()
    {
        var settings = new QqListenerSettings { ImportantKeywordsText = "作业" };
        var pipeline = new MessageEventPipeline(settings, new MessageRuleEvaluator(settings),
            new QqListenerDiagnosticsState(), NullLogger<MessageEventPipeline>.Instance, TimeProvider.System);
        var state = CoreState.Paused;
        var session = Guid.NewGuid();
        using var source = new NativeQqMessageSource(() => [], _ => throw new InvalidOperationException(),
            processEvent: message =>
            {
                var result = pipeline.Process(message with { EngineSession = session });
                return result.Accepted ? result.Event : null;
            }, getCoreState: () => state);

        var group = Wire.Concat(Wire.EncodeVarintField(1, 123456), Wire.EncodeBytesField(7, "测试群"));
        var response = Wire.Concat(Wire.EncodeVarintField(1, 1001), Wire.EncodeBytesField(8, group));
        var element = Wire.EncodeBytesField(1, Wire.EncodeBytesField(1, "明天交作业"));
        var body = Wire.EncodeBytesField(1, Wire.EncodeBytesField(2, element));
        foreach (var sequence in new ulong[] { 6243, 6244 })
        {
            var content = Wire.Concat(Wire.EncodeVarintField(1, 82), Wire.EncodeVarintField(5, sequence));
            var message = Wire.EncodeBytesField(1, Wire.Concat(Wire.EncodeBytesField(1, response),
                Wire.EncodeBytesField(2, content), Wire.EncodeBytesField(3, body)));
            var envelope = JsonSerializer.SerializeToUtf8Bytes(new { type = "recv", data = new
                { cmd = "trpc.msg.olpush.OlPushService.MsgPush", pb = Convert.ToHexString(message) } });
            var captured = SsoDecode.DecodeMessagePush(PmhqProtocol.ReadPush(envelope, "10000")!)!;
            captured.SourcePid = 42;
            source.Accept(captured);
        }
        Assert.Empty((await source.PollAsync(false, default)).AcceptedEvents);
        state = CoreState.Running;
        var digest = Assert.Single((await source.PollAsync(false, default)).AcceptedEvents);
        Assert.Equal(2, digest.PendingMessages.Count);
        Assert.All(digest.PendingMessages, message => Assert.Equal(session, message.EngineSession));
        Assert.True(digest.IsImportant);
        using var notifier = new ClassIslandMessageNotifier(settings);
        var request = notifier.BuildRequest(digest);
        Assert.Contains("作业", request.OverlayContent!.SpeechContent);
        Assert.Equal(TimeSpan.FromSeconds(settings.ImportantDurationSeconds), request.OverlayContent.Duration);
    }
}
