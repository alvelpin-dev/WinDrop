using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AirDrop.Server.Web;

/// <summary>Un fichero ofrecido para descarga desde el navegador.</summary>
public sealed record SharedFile(string Id, string Name, string Path, long Length);

/// <summary>
/// Servidor web local para intercambiar ficheros con dispositivos que no pueden hablar AirDrop.
/// </summary>
/// <remarks>
/// <para>
/// Esta es la vía que <b>sí funciona hoy con un iPhone</b> sin instalar nada en él: Safari viene
/// de serie. No es AirDrop y la interfaz no debe presentarlo como tal — el iPhone no descubre el
/// equipo solo, hay que abrir una dirección— pero cumple el objetivo práctico de mover ficheros
/// en ambos sentidos.
/// </para>
/// <para>
/// Se eligió esto en lugar de compartir por SMB porque crear un recurso compartido de Windows
/// exige permisos de administrador, y una aplicación portable no debería pedirlos.
/// </para>
/// <para>
/// Sirve por HTTP, no HTTPS: con un certificado autofirmado Safari muestra una advertencia de
/// seguridad que asusta y obliga a varios toques. El tráfico no sale de la red local y el
/// contenido lo elige el propio usuario.
/// </para>
/// </remarks>
public sealed class WebShareServer(ILogger<WebShareServer>? logger = null) : IAsyncDisposable
{
    private readonly ILogger<WebShareServer> _logger = logger ?? NullLogger<WebShareServer>.Instance;
    private readonly Dictionary<string, SharedFile> _shared = [];
    private readonly Lock _gate = new();

    private WebApplication? _application;
    private Func<string, Stream>? _uploadFactory;

    /// <summary>Puerto en el que escucha.</summary>
    public int Port { get; private set; }

    /// <summary>Se dispara cuando el navegador termina de subir un fichero.</summary>
    public event Action<string, long>? FileUploaded;

    /// <summary>Arranca el servidor.</summary>
    /// <param name="uploadFactory">Crea el destino donde escribir cada fichero subido.</param>
    /// <param name="port">Puerto a usar, o 0 para que lo asigne el sistema.</param>
    public async Task StartAsync(
        Func<string, Stream> uploadFactory,
        int port = 8771,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uploadFactory);

        if (_application is not null)
        {
            return;
        }

        _uploadFactory = uploadFactory;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = null;   // vídeos desde el móvil
            kestrel.Limits.MinRequestBodyDataRate = null;
            kestrel.ListenAnyIP(port);
        });

        _application = builder.Build();
        MapEndpoints(_application);

        await _application.StartAsync(cancellationToken).ConfigureAwait(false);

        Port = ResolveBoundPort() ?? port;
        _logger.LogInformation("Servidor web local activo en el puerto {Port}", Port);
    }

    /// <summary>Publica los ficheros que se ofrecerán para descarga.</summary>
    public void SetSharedFiles(IEnumerable<string> paths)
    {
        lock (_gate)
        {
            _shared.Clear();

            foreach (var path in paths)
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    continue;
                }

                // Identificador opaco: exponer la ruta real en la URL filtraría la estructura
                // de carpetas del equipo.
                var id = Guid.NewGuid().ToString("N")[..12];
                _shared[id] = new SharedFile(id, info.Name, info.FullName, info.Length);
            }
        }
    }

    /// <summary>Direcciones que el usuario puede abrir desde el móvil.</summary>
    public IReadOnlyList<string> GetAccessUrls()
    {
        var urls = new List<string>();

        foreach (var address in GetLanAddresses())
        {
            urls.Add($"http://{address}:{Port}/");
        }

        return urls;
    }

    private void MapEndpoints(WebApplication application)
    {
        application.MapGet("/", (Delegate)RenderIndex);
        application.MapGet("/download/{id}", (Delegate)Download);
        application.MapPost("/upload", (Delegate)UploadAsync);
    }

    private IResult RenderIndex()
    {
        List<SharedFile> files;
        lock (_gate)
        {
            files = [.. _shared.Values];
        }

        return Results.Content(BuildPage(files), "text/html; charset=utf-8");
    }

    private IResult Download(string id)
    {
        SharedFile? file;
        lock (_gate)
        {
            _shared.TryGetValue(id, out file);
        }

        if (file is null || !File.Exists(file.Path))
        {
            return Results.NotFound();
        }

        _logger.LogInformation("Descargando '{Name}' desde el navegador", file.Name);
        return Results.File(file.Path, "application/octet-stream", file.Name);
    }

    private async Task<IResult> UploadAsync(HttpContext context)
    {
        if (!context.Request.HasFormContentType)
        {
            return Results.BadRequest("Se esperaba un formulario.");
        }

        // Sin límite de tamaño por fichero: el iPhone sube vídeos de varios gigabytes.
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = null;
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
        var count = 0;

        foreach (var file in form.Files)
        {
            if (file.Length == 0)
            {
                continue;
            }

            // El nombre lo elige el navegador del móvil: se queda solo el nombre base para que no
            // pueda contener una ruta.
            var name = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            await using var destination = _uploadFactory!(name);
            await file.CopyToAsync(destination, context.RequestAborted).ConfigureAwait(false);

            _logger.LogInformation("Recibido '{Name}' ({Bytes} bytes) por el navegador", name, file.Length);
            FileUploaded?.Invoke(name, file.Length);
            count++;
        }

        return Results.Content(BuildUploadConfirmation(count), "text/html; charset=utf-8");
    }

    /// <summary>Direcciones IPv4 privadas del equipo, que son las que un móvil puede alcanzar.</summary>
    private static List<string> GetLanAddresses()
    {
        var addresses = new List<string>();

        foreach (var networkInterface in System.Net.NetworkInformation.NetworkInterface
            .GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus
                != System.Net.NetworkInformation.OperationalStatus.Up)
            {
                continue;
            }

            foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
            {
                var ip = unicast.Address;

                if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                    || IPAddress.IsLoopback(ip))
                {
                    continue;
                }

                addresses.Add(ip.ToString());
            }
        }

        return addresses;
    }

    private int? ResolveBoundPort()
    {
        var addresses = _application?.Services
            .GetService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            ?.Features
            .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()
            ?.Addresses;

        var first = addresses?.FirstOrDefault();
        return first is not null && Uri.TryCreate(first, UriKind.Absolute, out var uri)
            ? uri.Port
            : null;
    }

    /// <summary>
    /// Página servida al navegador del móvil.
    /// </summary>
    /// <remarks>
    /// Autocontenida a propósito: sin CDN, sin fuentes remotas y sin scripts externos, para que
    /// funcione en una red sin salida a internet y no filtre nada fuera del equipo.
    /// </remarks>
    private static string BuildPage(IReadOnlyList<SharedFile> files)
    {
        var list = new StringBuilder();

        if (files.Count == 0)
        {
            list.Append("<p class=\"empty\">No hay archivos compartidos ahora mismo.</p>");
        }
        else
        {
            list.Append("<ul class=\"files\">");
            foreach (var file in files)
            {
                list.Append(
                    $"<li><a href=\"/download/{file.Id}\" download>" +
                    $"<span class=\"name\">{WebUtility.HtmlEncode(file.Name)}</span>" +
                    $"<span class=\"size\">{FormatSize(file.Length)}</span></a></li>");
            }

            list.Append("</ul>");
        }

        return $$"""
        <!DOCTYPE html>
        <html lang="es">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
        <title>WinDrop</title>
        <style>
          :root { color-scheme: light dark; --accent: #0a84ff; }
          * { box-sizing: border-box; -webkit-tap-highlight-color: transparent; }
          body {
            margin: 0; padding: env(safe-area-inset-top) 20px 40px;
            font: -apple-system-body, system-ui, sans-serif;
            background: Canvas; color: CanvasText;
          }
          .wrap { max-width: 560px; margin: 0 auto; }
          h1 { font-size: 28px; font-weight: 700; margin: 32px 0 4px; }
          .sub { opacity: .6; margin: 0 0 28px; font-size: 15px; }
          h2 { font-size: 13px; text-transform: uppercase; letter-spacing: .06em;
               opacity: .55; margin: 28px 0 10px; }
          .card { background: color-mix(in srgb, CanvasText 6%, Canvas);
                  border-radius: 14px; padding: 4px; }
          .files { list-style: none; margin: 0; padding: 0; }
          .files li + li { border-top: 1px solid color-mix(in srgb, CanvasText 12%, transparent); }
          .files a { display: flex; justify-content: space-between; align-items: center;
                     gap: 12px; padding: 14px 16px; text-decoration: none; color: inherit; }
          .files a:active { background: color-mix(in srgb, CanvasText 8%, transparent); }
          .name { font-weight: 500; word-break: break-word; }
          .size { opacity: .5; font-size: 14px; white-space: nowrap; }
          .empty { opacity: .5; padding: 18px 16px; margin: 0; }
          form { margin: 0; }
          label.upload { display: block; text-align: center; padding: 22px 16px;
                         border: 2px dashed color-mix(in srgb, CanvasText 25%, transparent);
                         border-radius: 14px; font-weight: 500; }
          label.upload:active { border-color: var(--accent); color: var(--accent); }
          input[type=file] { display: none; }
          button { width: 100%; margin-top: 12px; padding: 15px; font-size: 17px;
                   font-weight: 600; border: 0; border-radius: 12px;
                   background: var(--accent); color: #fff; }
          button:disabled { opacity: .4; }
          .note { opacity: .45; font-size: 13px; margin-top: 28px; line-height: 1.5; }
        </style>
        </head>
        <body>
        <div class="wrap">
          <h1>WinDrop</h1>
          <p class="sub">Conectado a tu PC por la red local</p>

          <h2>Descargar al iPhone</h2>
          <div class="card">{{list}}</div>

          <h2>Enviar al PC</h2>
          <form method="post" action="/upload" enctype="multipart/form-data">
            <label class="upload">
              <input type="file" name="files" multiple onchange="
                var n = this.files.length;
                this.parentNode.textContent = n === 1
                  ? this.files[0].name
                  : n + ' archivos seleccionados';
                document.getElementById('send').disabled = n === 0;">
              Elegir fotos o archivos
            </label>
            <button id="send" type="submit" disabled>Enviar al PC</button>
          </form>

          <p class="note">
            Todo se transfiere directamente por tu red local.
            Nada pasa por internet ni por ningún servidor externo.
          </p>
        </div>
        </body>
        </html>
        """;
    }

    private static string BuildUploadConfirmation(int count) => $$"""
        <!DOCTYPE html>
        <html lang="es">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Enviado</title>
        <style>
          :root { color-scheme: light dark; }
          body { margin: 0; display: grid; place-items: center; min-height: 100vh;
                 font: -apple-system-body, system-ui, sans-serif;
                 background: Canvas; color: CanvasText; text-align: center; padding: 24px; }
          .tick { font-size: 56px; }
          h1 { font-size: 24px; margin: 16px 0 6px; }
          p { opacity: .6; margin: 0 0 28px; }
          a { display: inline-block; padding: 13px 28px; background: #0a84ff; color: #fff;
              text-decoration: none; border-radius: 12px; font-weight: 600; }
        </style>
        </head>
        <body>
          <div>
            <div class="tick">✓</div>
            <h1>{{(count == 1 ? "Archivo enviado" : $"{count} archivos enviados")}}</h1>
            <p>Ya están en tu PC</p>
            <a href="/">Volver</a>
          </div>
        </body>
        </html>
        """;

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    public async ValueTask DisposeAsync()
    {
        if (_application is not null)
        {
            await _application.StopAsync().ConfigureAwait(false);
            await _application.DisposeAsync().ConfigureAwait(false);
            _application = null;
        }

        lock (_gate)
        {
            _shared.Clear();
        }
    }
}
