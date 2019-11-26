using System;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;

namespace MockLoopbackSmtpServer
{
    class Program
    {
        static async Task Main()
        {
            const string Username = "Foo";
            const string Password = "Bar";

            using var server = new MockSmtpServer();
            using SmtpClient client = server.CreateClient();
            MailMessage msg = new MailMessage("foo@example.com", "bar@example.com", "hello", "howdydoo");

            CredentialCache cache = new CredentialCache();
            cache.Add("localhost", server.Port, "NTLM", new NetworkCredential(Username, Password));

            client.Credentials = cache;

            try
            {
                await client.SendMailAsync(msg);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                throw;
            }

            /*
            using var server = new MockSmtpServer();
            using var client = server.CreateClient();

            client.UseDefaultCredentials = false;
            client.Credentials = new NetworkCredential("Foo", "Bar");

            server.ReceiveMultipleConnections = true;

            // client.EnableSsl = true;

            server.OnConnected = socket     => Console.WriteLine("CONNECT: " + socket.RemoteEndPoint.ToString());
            server.OnHelloReceived = hello  => Console.WriteLine("HELLO: " + hello);
            server.OnUnknownCommand = msg   => Console.WriteLine("UNKNOWN: " + msg);
            server.OnQuitReceived = socket  => Console.WriteLine("QUIT: " + socket.RemoteEndPoint.ToString());

            // server.OnCommandReceived = (cmd, arg) => Console.WriteLine($"CMD {cmd}: {arg}");

            var message = new MailMessage("miha.zupan@microsoft.com", "someone@internet.com", "Foo subject", "Foo body");
            await client.SendMailAsync(message);
            PrintDebug(server);

            client.SendAsyncCancel();

            message = new MailMessage("miha.zupan@microsoft.com", "someone.else@interwebz.com", "Bar subject", "Bar body");
            await client.SendMailAsync(message);
            PrintDebug(server);

            server.Dispose();
            Console.WriteLine("All done");
            await Task.Delay(int.MaxValue);
            */
        }

        static void PrintDebug(MockSmtpServer server)
        {
            Console.WriteLine("HELLO: " + server.ClientHello);
            Console.WriteLine("FROM: " + server.From);
            Console.WriteLine("TO: " + server.To);
            Console.WriteLine("Auth used: " + (server.AuthMethodUsed ?? "None"));
            Console.WriteLine("UserPass: " + (server.UsernamePassword ?? "None"));

            Console.WriteLine();
            Console.WriteLine("Message headers:");
            foreach (var header in server.Message.Headers)
            {
                Console.WriteLine($"{header.Key}: {header.Value}");
            }
            Console.WriteLine();
            Console.WriteLine("MSG FROM: " + server.Message.From);
            Console.WriteLine("MSG TO: " + server.Message.To);
            Console.WriteLine("MSG SUBJECT: " + server.Message.Subject);
            Console.WriteLine();
            Console.WriteLine("Body:");
            Console.WriteLine(server.Message.Body);
        }
    }
}
