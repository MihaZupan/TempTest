﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MockLoopbackSmtpServer
{
    public class MockSmtpServer : IDisposable
    {
        private static readonly ReadOnlyMemory<byte> MessageTerminator = new byte[] { (byte)'\r', (byte)'\n' };
        private static readonly ReadOnlyMemory<byte> BodyTerminator = new byte[] { (byte)'\r', (byte)'\n', (byte)'.', (byte)'\r', (byte)'\n' };

        public bool ReceiveMultipleConnections = false;
        public bool SupportSmtpUTF8 = false;

        private bool _disposed = false;
        private readonly Socket _listenSocket;
        private readonly ConcurrentBag<Socket> _socketsToDispose;
        private long _messageCounter = new Random().Next(1000, 2000);

        public readonly int Port;
        public SmtpClient CreateClient() => new SmtpClient("localhost", Port);

        public Action<Socket> OnConnected;
        public Action<string> OnHelloReceived;
        public Action<string, string> OnCommandReceived;
        public Action<string> OnUnknownCommand;
        public Action<Socket> OnQuitReceived;

        public string ClientHello { get; private set; }
        public string From { get; private set; }
        public string To { get; private set; }
        public string UsernamePassword { get; private set; }
        public string Username { get; private set; }
        public string Password { get; private set; }
        public string AuthMethodUsed { get; private set; }
        public ParsedMailMessage Message { get; private set; }

        public int ConnectionCount { get; private set; }
        public int MessagesReceived { get; private set; }

        public MockSmtpServer()
        {
            _socketsToDispose = new ConcurrentBag<Socket>();
            _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _socketsToDispose.Add(_listenSocket);

            _listenSocket.Bind(new IPEndPoint(IPAddress.Any, 0));
            Port = ((IPEndPoint)_listenSocket.LocalEndPoint).Port;
            _listenSocket.Listen(1);

            _ = Task.Run(async () =>
            {
                do
                {
                    var socket = await _listenSocket.AcceptAsync();
                    _socketsToDispose.Add(socket);
                    ConnectionCount++;
                    _ = Task.Run(async () => await HandleConnectionAsync(socket));
                }
                while (ReceiveMultipleConnections);
            });
        }

        private async Task HandleConnectionAsync(Socket socket)
        {
            var buffer = new byte[1024].AsMemory();

            async ValueTask<string> ReceiveMessageAsync(bool isBody = false)
            {
                var terminator = isBody ? BodyTerminator : MessageTerminator;
                int suffix = terminator.Length;

                int received = 0;
                do
                {
                    int read = await socket.ReceiveAsync(buffer.Slice(received), SocketFlags.None);
                    if (read == 0) return null;
                    received += read;
                }
                while (received < suffix || !buffer.Slice(received - suffix, suffix).Span.SequenceEqual(terminator.Span));

                MessagesReceived++;
                return Encoding.UTF8.GetString(buffer.Span.Slice(0, received - suffix));
            }
            async ValueTask SendMessageAsync(string text)
            {
                var bytes = buffer.Slice(0, Encoding.UTF8.GetBytes(text, buffer.Span) + 2);
                bytes.Span[^2] = (byte)'\r';
                bytes.Span[^1] = (byte)'\n';
                await socket.SendAsync(bytes, SocketFlags.None);
            }

            try
            {
                OnConnected?.Invoke(socket);
                await SendMessageAsync("220 localhost");

                string message = await ReceiveMessageAsync();
                Debug.Assert(message.ToLower().StartsWith("helo ") || message.ToLower().StartsWith("ehlo "));
                ClientHello = message.Substring(5);
                OnCommandReceived?.Invoke(message.Substring(0, 4), ClientHello);
                OnHelloReceived?.Invoke(ClientHello);

                await SendMessageAsync("250-localhost, mock server here");
                if (SupportSmtpUTF8) await SendMessageAsync("250-SMTPUTF8");
                await SendMessageAsync("250 AUTH PLAIN LOGIN NTLM");

                while ((message = await ReceiveMessageAsync()) != null)
                {
                    int colonIndex = message.IndexOf(':');
                    string command = colonIndex == -1 ? message : message.Substring(0, colonIndex);
                    string argument = command.Length == message.Length ? string.Empty : message.Substring(colonIndex + 1).Trim();

                    OnCommandReceived?.Invoke(command, argument);

                    if (command.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = command.Split(' ');
                        Debug.Assert(parts.Length > 1, "Expected an actual auth request");

                        AuthMethodUsed = parts[1];

                        // PLAIN is not supported by SmtpClient
                        /*
                        if (parts[1].Equals("PLAIN", StringComparison.OrdinalIgnoreCase))
                        {
                            string base64;
                            if (parts.Length == 2)
                            {
                                await SendMessageAsync("334");
                                base64 = await ReceiveMessageAsync();
                            }
                            else
                            {
                                base64 = parts[2];
                            }
                            UsernamePassword = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                            await SendMessageAsync("235 Authentication successful");
                        }
                        else
                        */
                        if (parts[1].Equals("LOGIN", StringComparison.OrdinalIgnoreCase))
                        {
                            if (parts.Length == 2)
                            {
                                await SendMessageAsync("334 VXNlcm5hbWU6");
                                Username = GetStringFromBase64(await ReceiveMessageAsync());
                            }
                            else
                            {
                                Username = GetStringFromBase64(parts[2]);
                            }
                            await SendMessageAsync("334 UGFzc3dvcmQ6");
                            Password = GetStringFromBase64(await ReceiveMessageAsync());
                            UsernamePassword = Username + Password;
                            await SendMessageAsync("235 Authentication successful");
                        }
                        else if (parts[1].Equals("NTLM", StringComparison.OrdinalIgnoreCase))
                        {
                            await SendMessageAsync("500 I lied, I can't speak NTLM - here's an invalid response");
                        }
                        else await SendMessageAsync("504 scheme not supported");
                        continue;
                    }

                    switch (command.ToUpper())
                    {
                        case "MAIL FROM":
                            From = argument;
                            await SendMessageAsync("250 Ok");
                            break;

                        case "RCPT TO":
                            To = argument;
                            await SendMessageAsync("250 Ok");
                            break;

                        case "DATA":
                            await SendMessageAsync("354 Start mail input; end with <CRLF>.<CRLF>");
                            string data = await ReceiveMessageAsync(true);
                            Message = ParsedMailMessage.Parse(data);
                            await SendMessageAsync("250 Ok: queued as " + Interlocked.Increment(ref _messageCounter));
                            break;

                        case "QUIT":
                            OnQuitReceived?.Invoke(socket);
                            await SendMessageAsync("221 Bye");
                            return;

                        default:
                            OnUnknownCommand?.Invoke(message);
                            await SendMessageAsync("500 Idk that command");
                            break;
                    }
                }
            }
            catch { }
            finally
            {
                try
                {
                    socket.Shutdown(SocketShutdown.Both);
                }
                finally
                {
                    socket?.Close();
                }
            }
        }

        private static string GetStringFromBase64(string base64)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                foreach (var socket in _socketsToDispose)
                {
                    try
                    {
                        socket.Close();
                    }
                    catch { }
                }
                _socketsToDispose.Clear();
            }
        }


        public class ParsedMailMessage
        {
            public readonly IReadOnlyDictionary<string, string> Headers;
            public readonly string Body;

            private string GetHeader(string name) => Headers.TryGetValue(name, out string value) ? value : "NOT-PRESENT";
            public string From => GetHeader("From");
            public string To => GetHeader("To");
            public string Subject => GetHeader("Subject");

            private ParsedMailMessage(Dictionary<string, string> headers, string body)
            {
                Headers = headers;
                Body = body;
            }

            public static ParsedMailMessage Parse(string data)
            {
                Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                ReadOnlySpan<char> dataSpan = data;
                string body = null;

                while (!dataSpan.IsEmpty)
                {
                    int endOfLine = dataSpan.IndexOf('\n');
                    Debug.Assert(endOfLine != -1, "Expected valid \r\n terminated lines");
                    var line = dataSpan.Slice(0, endOfLine).TrimEnd('\r');

                    if (line.IsEmpty)
                    {
                        body = dataSpan.Slice(endOfLine + 1).TrimEnd(stackalloc char[] { '\r', '\n' }).ToString();
                        break;
                    }
                    else
                    {
                        int colon = line.IndexOf(':');
                        Debug.Assert(colon != -1, "Expected a valid header");
                        headers.Add(line.Slice(0, colon).Trim().ToString(), line.Slice(colon + 1).Trim().ToString());
                        dataSpan = dataSpan.Slice(endOfLine + 1);
                    }
                }

                return new ParsedMailMessage(headers, body);
            }
        }
    }
}
