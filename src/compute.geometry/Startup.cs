using System;
using System.Threading.Tasks;
using Microsoft.Owin;
using Microsoft.Owin.Cors;
using Owin;

[assembly: OwinStartup(typeof(compute.geometry.Startup))]

namespace compute.geometry
{
    public class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            app.UseCors(CorsOptions.AllowAll);
            app.Use<LoggingMiddleware>();
            app.UseNancy();
        }
    }

    /// <summary>
    /// Custom request logging for debugging.
    /// </summary>
    internal class LoggingMiddleware : OwinMiddleware
    {
        public LoggingMiddleware(OwinMiddleware next)
            : base(next)
        {
        }

        public override async Task Invoke(IOwinContext ctx)
        {
            IOwinRequest req = ctx.Request;


            // Exclude /isbusy endpoint from incrementing the counter
            bool incrementCounter = req.Uri.AbsolutePath != "/isbusy";

            if (incrementCounter)
            {
                // Increment the active request counter
                BusyStatus.Increment();
            }

            try
            {
                // Invoke the next middleware in the pipeline
                await Next.Invoke(ctx);
            }
            finally
            {
                if (incrementCounter)
                {
                    // Decrement the active request counter
                    BusyStatus.Decrement();
                }

                IOwinResponse res = ctx.Response;
                string contentLength = res.ContentLength > -1 ? res.ContentLength.ToString() : "-";

                // Corrected the condition to use && instead of ||
                if (req.Uri.AbsolutePath != "/healthcheck" && req.Uri.AbsolutePath != "/favicon.ico")
                {
                    // Log request in Apache format
                    string msg = $"{req.RemoteIpAddress} - [{DateTime.Now:o}] \"{req.Method} {req.Uri.AbsolutePath} {req.Protocol}\" {res.StatusCode} {contentLength}";
                    Serilog.Log.Information(msg);
                }
            }
        }
    }
}
