using System;
using System.Text.Json;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using RevitCli.Shared;

namespace RevitCli.Addin.Handlers;

public class ScheduleController : WebApiController
{
    private readonly IRevitOperations _operations;

    public ScheduleController(IRevitOperations operations)
    {
        _operations = operations;
    }

    [Route(HttpVerbs.Get, "/schedules")]
    public async Task ListSchedules()
    {
        HttpContext.Response.ContentType = "application/json";
        using var writer = HttpContext.OpenResponseText();

        try
        {
            var schedules = await _operations.ListSchedulesAsync();
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleInfo[]>.Ok(schedules)));
        }
        catch (InvalidOperationException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleInfo[]>.Fail(ex.Message)));
        }
        catch (Exception ex)
        {
            HttpContext.Response.StatusCode = 500;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleInfo[]>.Fail(ex.Message)));
        }
    }

    [Route(HttpVerbs.Post, "/schedules/export")]
    public async Task ExportSchedule()
    {
        HttpContext.Response.ContentType = "application/json";
        using var writer = HttpContext.OpenResponseText();

        ScheduleExportRequest? request;
        try
        {
            var body = await HttpContext.GetRequestBodyAsStringAsync();
            request = JsonSerializer.Deserialize<ScheduleExportRequest>(body);
        }
        catch (JsonException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleData>.Fail($"Invalid JSON: {ex.Message}")));
            return;
        }

        if (request == null)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleData>.Fail("Request body is required")));
            return;
        }

        try
        {
            var result = await _operations.ExportScheduleAsync(request);
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleData>.Ok(result)));
        }
        catch (ArgumentException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleData>.Fail(ex.Message)));
        }
        catch (InvalidOperationException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleData>.Fail(ex.Message)));
        }
        catch (Exception ex)
        {
            HttpContext.Response.StatusCode = 500;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleData>.Fail(ex.Message)));
        }
    }

    [Route(HttpVerbs.Post, "/schedules/create")]
    public async Task CreateSchedule()
    {
        HttpContext.Response.ContentType = "application/json";
        using var writer = HttpContext.OpenResponseText();

        ScheduleCreateRequest? request;
        try
        {
            var body = await HttpContext.GetRequestBodyAsStringAsync();
            request = JsonSerializer.Deserialize<ScheduleCreateRequest>(body);
        }
        catch (JsonException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleCreateResult>.Fail($"Invalid JSON: {ex.Message}")));
            return;
        }

        if (request == null)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleCreateResult>.Fail("Request body is required")));
            return;
        }

        try
        {
            var result = await _operations.CreateScheduleAsync(request);
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleCreateResult>.Ok(result)));
        }
        catch (ArgumentException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleCreateResult>.Fail(ex.Message)));
        }
        catch (InvalidOperationException ex)
        {
            HttpContext.Response.StatusCode = 409;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleCreateResult>.Fail(ex.Message)));
        }
        catch (Exception ex)
        {
            HttpContext.Response.StatusCode = 500;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleCreateResult>.Fail(ex.Message)));
        }
    }
}
