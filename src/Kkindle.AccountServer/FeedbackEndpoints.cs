using System.Text;
using Kkindle.Core;
using Microsoft.AspNetCore.Http.Features;

namespace Kkindle.AccountServer;

/// <summary>Feedback API for the in-repo SQLite protocol server. The production server mirrors this contract.</summary>
internal static class FeedbackEndpoints
{
    private const long MaximumRequestBytes =
        FeedbackLimits.MaxTotalAttachmentBytes + FeedbackLimits.MaxSoftwareLogBytes + 512 * 1024;

    public static void Map(WebApplication app, AccountDatabase store, FeedbackStorage storage)
    {
        app.MapPost("/api/v1/feedback", async (HttpContext context, CancellationToken token) =>
        {
            var session = await store.GetSessionAsync(AccountServerHost.Bearer(context), token);
            if (context.Request.ContentLength is { } declared && declared > MaximumRequestBytes)
                throw new AccountRequestException(StatusCodes.Status413PayloadTooLarge, "截图总大小超过限制。");
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = MaximumRequestBytes;
            if (!context.Request.HasFormContentType)
                throw new AccountRequestException(StatusCodes.Status400BadRequest, "请使用表单提交反馈。");
            IFormCollection form;
            try { form = await context.Request.ReadFormAsync(token); }
            catch (Exception exception) when (exception is BadHttpRequestException or InvalidDataException)
            { throw new AccountRequestException(StatusCodes.Status413PayloadTooLarge, "反馈内容或截图过大。"); }
            var draft = ReadDraft(form);
            if (FeedbackValidation.ValidateDraft(draft) is { } invalid)
                throw new AccountRequestException(StatusCodes.Status400BadRequest, invalid);
            if (await store.FindFeedbackAsync(session.User.Id, draft.RequestId, token) is { } existing)
                return Results.Json(existing, AccountJsonContext.Default.FeedbackReceipt);

            var files = form.Files.GetFiles("attachments");
            if (files.Count > FeedbackLimits.MaxAttachments)
                throw new AccountRequestException(StatusCodes.Status400BadRequest,
                    $"最多只能添加 {FeedbackLimits.MaxAttachments} 张截图。");
            var images = new List<FeedbackImage>();
            long total = 0;
            foreach (var file in files)
            {
                if (file.Length > FeedbackLimits.MaxAttachmentBytes)
                    throw new AccountRequestException(StatusCodes.Status413PayloadTooLarge, "每张截图不能超过 4 MB。");
                if (file.Length == 0) throw new AccountRequestException(StatusCodes.Status400BadRequest, "截图内容为空。");
                total += file.Length;
                if (total > FeedbackLimits.MaxTotalAttachmentBytes)
                    throw new AccountRequestException(StatusCodes.Status413PayloadTooLarge, "截图总大小超过限制。");
                await using var input = file.OpenReadStream();
                using var memory = new MemoryStream((int)file.Length);
                await input.CopyToAsync(memory, token);
                var bytes = memory.ToArray();
                var contentType = FeedbackValidation.DetectImageType(bytes)
                    ?? throw new AccountRequestException(StatusCodes.Status400BadRequest,
                        "截图格式无效，仅支持 JPEG、PNG 或 WebP。");
                images.Add(new(file.FileName ?? "", contentType, bytes));
            }
            if (FeedbackValidation.ValidateImages(images) is { } imageInvalid)
                throw new AccountRequestException(StatusCodes.Status400BadRequest, imageInvalid);
            var softwareLog = form["softwareLog"].ToString().Trim();
            if (FeedbackValidation.ValidateSoftwareLog(softwareLog) is { } logInvalid)
                throw new AccountRequestException(StatusCodes.Status400BadRequest, logInvalid);

            var feedbackId = Guid.NewGuid();
            var keys = new List<string>();
            try
            {
                foreach (var image in images)
                {
                    var storageId = Guid.NewGuid();
                    using var input = new MemoryStream(image.Bytes, writable: false);
                    keys.Add(await storage.SaveAsync(feedbackId, storageId, image.ContentType, input, image.Bytes.LongLength, token));
                }
                if (softwareLog.Length > 0)
                {
                    var logBytes = Encoding.UTF8.GetBytes(softwareLog);
                    using var logInput = new MemoryStream(logBytes, writable: false);
                    keys.Add(await storage.SaveLogAsync(feedbackId, Guid.NewGuid(), logInput, logBytes.LongLength, token));
                }
                var receipt = await store.CreateFeedbackAsync(feedbackId, session.User, draft, keys, token);
                // A concurrent identical request may have won the unique (user, request) insert.
                if (receipt.Id != feedbackId)
                {
                    foreach (var key in keys) storage.Delete(key);
                    storage.DeleteFeedback(feedbackId);
                }
                return Results.Json(receipt, AccountJsonContext.Default.FeedbackReceipt,
                    statusCode: receipt.Id == feedbackId ? StatusCodes.Status201Created : StatusCodes.Status200OK);
            }
            catch
            {
                foreach (var key in keys) storage.Delete(key);
                storage.DeleteFeedback(feedbackId);
                throw;
            }
        }).RequireRateLimiting("feedback");

        app.MapGet("/api/v1/feedback", async (HttpContext context, CancellationToken token) =>
        {
            var session = await store.GetSessionAsync(AccountServerHost.Bearer(context), token);
            return Results.Json(await store.ListFeedbackAsync(session.User.Id, token),
                AccountJsonContext.Default.FeedbackItemArray);
        });

        app.MapGet("/api/v1/feedback/{id:guid}", async (Guid id, HttpContext context, CancellationToken token) =>
        {
            var session = await store.GetSessionAsync(AccountServerHost.Bearer(context), token);
            return Results.Json(await store.GetFeedbackAsync(session.User.Id, id, token),
                AccountJsonContext.Default.FeedbackItem);
        });
    }

    private static FeedbackDraft ReadDraft(IFormCollection form)
    {
        Guid.TryParse(form["requestId"].ToString(), out var requestId);
        var platform = form["platform"].ToString().Trim();
        var platformVersion = form["platformVersion"].ToString().Trim();
        // Older iOS clients may send iosVersion without a platform field.
        if (platform.Length == 0 && form["iosVersion"].ToString().Length > 0) platform = "ios";
        if (platformVersion.Length == 0) platformVersion = form["iosVersion"].ToString().Trim();
        return new()
        {
            RequestId = requestId,
            Type = form["type"].ToString().Trim(),
            Content = form["content"].ToString(),
            Contact = form["contact"].ToString(),
            AppVersion = form["appVersion"].ToString().Trim(),
            Platform = platform,
            PlatformVersion = platformVersion,
            DeviceModel = form["deviceModel"].ToString().Trim()
        };
    }
}
