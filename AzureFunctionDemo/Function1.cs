using Azure;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Web;

namespace AzureFunctionDemo;

public class Function1
{
    private readonly ILogger<Function1> _logger;

    private readonly string _connectionString;
    private readonly string _containerName = "videos";   // container name
    private readonly string _blobName = "sample.mp4";    // file name

    public Function1(ILogger<Function1> logger, IConfiguration configuration)
    {
        _logger = logger;
         //configuration["BLOB_CONNECTION_STRING"];
         _connectionString = "";
    }

    [Function("Function1")]
    public IActionResult Run([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
    {
        _logger.LogInformation("C# HTTP trigger function processed a request.");
        return new OkObjectResult("Welcome to Azure Functions!");
    }

    [Function("GetHLSPlaylist")]
    public async Task<HttpResponseData> GetHLSPlaylist(
           [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "hls/{videoName}.m3u8")] HttpRequestData req,
           string videoName)
    {
        var response = req.CreateResponse();

        try
        {
            _logger.LogInformation($"Fetching playlist for video: {videoName}");

            var blobClient = new BlobClient(_connectionString, _containerName, $"{videoName}.m3u8");

            if (!await blobClient.ExistsAsync())
            {
                _logger.LogWarning($"Playlist not found: {videoName}.m3u8");
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteStringAsync("Playlist not found");
                return notFound;
            }

            var playlistContent = await blobClient.DownloadContentAsync();
            var content = playlistContent.Value.Content.ToString();

            // Build the correct base URL dynamically (Azure Function host)
            var baseUrl = $"{req.Url.Scheme}://{req.Url.Host}/api/seg/{videoName}/";

            // Rewrite relative segment URLs (e.g., sample0.ts → https://.../api/seg/videoName/sample0.ts)
            content = System.Text.RegularExpressions.Regex.Replace(content, @"(^|\n)([^#][^\n]*)", match =>
            {
                var line = match.Groups[2].Value.Trim();
                if (line.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                {
                    // Encode the segment name
                    var encoded = HttpUtility.UrlEncode(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(line)));

                    return $"{match.Groups[1].Value}{baseUrl}{encoded}";
                }
                return match.Value;
            });

            response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/vnd.apple.mpegurl");
            await response.WriteStringAsync(content);
            return response;
        }
        catch (Exception ex)
        {
            response.StatusCode = HttpStatusCode.InternalServerError;
            await response.WriteStringAsync($"Error: {ex.Message}\n{ex.StackTrace}");
            _logger.LogError(ex, "Error in GetVideo function");
        }
        return response;
    }

    [Function("GetSegment")]
    public async Task<HttpResponseData> GetSegment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "seg/{videoName}/{segment}")] HttpRequestData req,
        string videoName,
        string segment, FunctionContext context)
    {
        var response = req.CreateResponse();

        var logger = context.GetLogger("GetSegment");

        logger.LogInformation($"[GetSegment] Requested videoName: {videoName}, segment: {segment}");


        try
        {
            var decodedBytes = Convert.FromBase64String(HttpUtility.UrlDecode(segment));
            var decodedSegment = System.Text.Encoding.UTF8.GetString(decodedBytes);



            var blobClient = new BlobClient(_connectionString, _containerName, decodedSegment);
           
            if (!await blobClient.ExistsAsync())
            {
                logger.LogWarning($"[GetSegment] Segment not found in container '{_containerName}' with name '{segment}'");

                response.StatusCode = HttpStatusCode.NotFound;
                await response.WriteStringAsync("Segment not found");
                return response;
            }
            logger.LogInformation($"[GetSegment] Segment found, streaming to client...");

            response.Headers.Add("Content-Type", "video/MP2T"); // TS content type
            var stream = await blobClient.OpenReadAsync();
            await stream.CopyToAsync(response.Body);
        }
        catch (Exception ex)
        {
            response.StatusCode = HttpStatusCode.InternalServerError;
            await response.WriteStringAsync($"Error: {ex.Message}\n{ex.StackTrace}");
            _logger.LogError(ex, "Error in GetVideo function");
        }
        return response;
    }

    [Function("GetVideo")]
    public async Task<HttpResponseData> GetVideo(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
    {
        var response = req.CreateResponse(HttpStatusCode.OK); ;
        try
        {
            // Create Blob client
            var blobClient = new BlobClient(_connectionString, _containerName, _blobName);

            // Download blob into a memory stream
            var stream = new MemoryStream();
            await blobClient.DownloadToAsync(stream);
            stream.Position = 0;

            // Create HTTP response and return video stream
            response.Headers.Add("Content-Type", "video/mp4");
            await stream.CopyToAsync(response.Body);
        }
        catch (Exception ex)
        {
            response.StatusCode = HttpStatusCode.InternalServerError;
            await response.WriteStringAsync($"Error: {ex.Message}\n{ex.StackTrace}");
            _logger.LogError(ex, "Error in GetVideo function");
        }
        return response;
    }
}
