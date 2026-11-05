using MyTelegram.Services.Extensions;
using System.Diagnostics;

namespace MyTelegram.Services.Services;

public class DefaultDataProcessor<TData>(
    IHandlerHelper handlerHelper,
    IObjectMessageSender objectMessageSender,
    ILogger<DefaultDataProcessor<TData>> logger,
    IExceptionProcessor exceptionProcessor,
    IRequestHelper requestHelper,
    IInvokeAfterMsgProcessor invokeAfterMsgProcessor,
    // Injected as a collection so hosts that serve requests without the messenger services
    // registered (which is where the validator lives) still resolve this processor.
    IEnumerable<IFromMessageContextValidator>? fromMessageContextValidators = null)
    : IDataProcessor<TData>
    where TData : DataReceivedEvent
{
    public virtual Task ProcessAsync(TData obj)
    {
        Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            if (handlerHelper.TryGetHandler(obj.ObjectId, out var handler))
            {

                IObject? data = null;
                var req = GetRequestInput(obj);
                try
                {
                    data = GetData(obj);

                    if (BotMethodPolicy.IsForbiddenForBot(req.UserId, data))
                    {
                        RpcErrors.RpcErrors400.BotMethodInvalid.ThrowRpcError();
                    }

                    bool needToCheckRequest = handler is IDistinctObjectHandler ||
                                              ObjectIdConsts.CommandServerHandlers.ContainsKey(req.ObjectId);

                    if (!needToCheckRequest && data is IHasSubQuery subQuery)
                    {
                        // Recurses to the innermost query so any depth of invoke* nesting is unwrapped.
                        // The old two-level walk let a triple-nested wrapper (e.g.
                        // invokeWithoutUpdates(invokeWithReCaptcha(invokeWithBusinessConnection(...))))
                        // skip the replay/dedup gate on state-changing commands.
                        needToCheckRequest =
                            ObjectIdConsts.CommandServerHandlers.ContainsKey(subQuery.GetObjectId());
                    }

                    if (needToCheckRequest)
                    {
                        if (!await requestHelper.CheckRequestAsync(req))
                        {
                            return;
                        }
                    }

                    // The input*FromMessage constructors replace an access hash with a cited message
                    // context, so they are the one input the access-hash check cannot cover. Proving
                    // the context here means no handler can accept one unproven.
                    // See https://corefork.telegram.org/api/min
                    foreach (var validator in fromMessageContextValidators ?? [])
                    {
                        await validator.ValidateAsync(req, data);
                    }

                    var handlerName = handler.GetType().Name;
                    // Updates.getDifference and langpack.getDifference use the same handler name
                    if (req.ObjectId == 0xcd984aa5)
                    {
                        handlerName = $"{handlerName}(langpack)";
                    }

                    if (data is IHasSubQuery)
                    {
                        handlerName = handlerHelper.GetHandlerFullName(data);
                    }

                    var r = await handler.HandleAsync(req, data);
                    sw.Stop();

                    logger.UserRequestHandled(
                        obj.UserId,
                        req.ReqMsgId,
                        handlerName,
                        obj.DeviceType,
                        obj.Layer,
                        sw.Elapsed.TotalMilliseconds
                    );

                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug(
                            "User {UserId} {Handler} {DeviceType}[{Layer}] {ElapsedMs}ms. [{@Input}] Request data: {@Request}, Response: {@Response}",
                            obj.UserId,
                            handlerName,
                            obj.DeviceType,
                            obj.Layer,
                            sw.Elapsed.TotalMilliseconds,
                            req,
                            data,
                            r
                        );
                    }

                    if (r != null!)
                    {
                        await SendMessageToPeerAsync(GetRequestInput(obj).ToRequestInfo(), r);
                    }

                    await invokeAfterMsgProcessor.AddCompletedReqMsgIdAsync(obj.ReqMsgId);
                }
                catch (Exception ex)
                {
                    await exceptionProcessor.HandleExceptionAsync(ex, req, data,
                        handler.GetType().Name);
                }
                finally
                {
                    if (obj is IMayHaveMemoryOwner mayHaveMemoryOwner)
                    {
                        mayHaveMemoryOwner.MemoryOwner?.Dispose();
                    }
                }
            }
        });

        return Task.CompletedTask;
    }

    protected virtual IObject GetData(TData obj)
    {
        return obj.Data.ToTObject<IObject>();
    }

    protected virtual IRequestInput GetRequestInput(TData obj)
    {
        var req = new RequestInput(
            obj.ConnectionId,
            obj.ConnectionType,
            RequestIdHelper.ForMessage(obj.RequestId, obj.ReqMsgId),
            obj.ObjectId,
            obj.ReqMsgId,
            obj.SeqNumber,
            obj.UserId,
            obj.AuthKeyId,
            obj.PermAuthKeyId,
            obj.Layer,
            obj.Date,
            obj.DeviceType,
            obj.ClientIp,
            obj.SessionId,
            obj.AccessHashKeyId
        );

        return req;
    }

    protected virtual Task SendMessageToPeerAsync(RequestInfo requestInfo,
        IObject data)
    {
        return objectMessageSender.SendMessageToPeerAsync(requestInfo, data);
    }
}