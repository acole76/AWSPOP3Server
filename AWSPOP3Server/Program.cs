using ArgumentParser;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace POP3DotNet
{
	class Program
	{
    private static Config config;
    private static IPostOfficeProtocol mailStore;

    static void Main(string[] args)
		{
      ArgParse argparse = new ArgParse
      (
          new ArgItem("ip-address", "i", false, "ip address to bind on", "0.0.0.0", ArgParse.ArgParseType.String),
          new ArgItem("port", "p", false, "username", "110", ArgParse.ArgParseType.Int),
          new ArgItem("mail-store-type", "t", false, "Indicates where the mail store is located", "AWS", ArgParse.ArgParseType.Choice, new string[] { "filesystem", "aws" })
      );

      argparse.parse(args);

      string ipString = argparse.Get<string>("ip-address");
      int port = argparse.Get<int>("port");
      string mailStoreType = argparse.Get<string>("mail-store-type");

      IPAddress ip = IPAddress.Parse(ipString);

      TcpListener serverSocket = new TcpListener(ip, port);
      TcpClient clientSocket = new TcpClient();

      serverSocket.Start();

      try
      {
        while (true)
        {
          clientSocket = serverSocket.AcceptTcpClient();
          ClientHandler client = new ClientHandler();
          client.startClient(getDriver(clientSocket, mailStoreType));
        }
      }
      catch(Exception e)
      {
        Console.WriteLine(e.Message);
      }
      finally
      {
        clientSocket.Close();
        serverSocket.Stop();
      }
    }

    private static IPostOfficeProtocol getDriver(TcpClient clientSocket, string driverType)
    {
      config = new Config();
      switch (driverType.ToLower())
      {
        case "aws":
          config.Settings.Add("awskey", Environment.GetEnvironmentVariable("awskey"));
          config.Settings.Add("awssecret", Environment.GetEnvironmentVariable("awssecret"));
          config.Settings.Add("bucket", Environment.GetEnvironmentVariable("bucket"));
          config.Settings.Add("prefix", Environment.GetEnvironmentVariable("prefix"));
          mailStore = new AwsSESImpl(clientSocket, config);
          break;
        case "filesystem":
          config.Settings.Add("emailFolder", Environment.GetEnvironmentVariable("email_folder"));
          mailStore = new FileSystemImpl(clientSocket, config);
          break;
        default:
          throw new Exception("unsupported mail store");
      }

      return mailStore;
    }
	}

  public class Config
  {
    public Dictionary<string,string> Settings { get; set; }

    public Config()
    {
      Settings = new Dictionary<string, string>();
    }
  }

  public class ClientHandler
  {
    public IPostOfficeProtocol driver;

    public void startClient(IPostOfficeProtocol _driver)
    {
      driver = _driver;
      Thread clientThread = new Thread(doPostOfficeProtocol);
      clientThread.Start();
    }

    private string trimBuffer(byte[] buffer)
    {
      return System.Text.Encoding.ASCII.GetString(buffer).Replace("\0", string.Empty).Replace("\n", string.Empty).Replace("\r", string.Empty).Trim();
    }

    private void doPostOfficeProtocol()
    {
      byte[] buffer = new byte[driver.client.ReceiveBufferSize];

      driver.stream = driver.client.GetStream();
      driver.SendBuffer("+OK POP3 server ready");

      while (true)
      {
        try
        {
          driver.stream.Read(buffer, 0, (int)driver.client.ReceiveBufferSize);
          
          string incomingString = trimBuffer(buffer);
          string command = incomingString.Split(' ').First<string>();
          string data = string.Join(" ", incomingString.Split(' ').Skip<string>(1).ToArray<string>());

          Trace.TraceInformation(string.Format("bufferString -> {0}", incomingString));
          Trace.TraceInformation(string.Format("{0} -> {1}", command, data));
          switch (command.ToLower())
          {
            case "quit":
              driver.Quit();
              return; //can't break loop from here
            case "stat":
              driver.Stat(data);
              break;
            case "list":
              driver.List(data);
              break;
            case "retr":
              driver.Retr(data);
              break;
            case "user":
              driver.User(data);
              break;
            case "pass":
              driver.Pass(data);
              break;
            case "dele":
              driver.Dele(data);
              break;
            case "rset":
              driver.Rset(data);
              break;
            case "top":
              driver.Top(data);
              break;
            case "uidl":
              driver.Uidl(data);
              break;
            case "apop":
              driver.Apop(data);
              break;
            default:
              driver.SendBuffer("-ERR Unknown command.");
              break;
          }

          buffer = new byte[driver.client.ReceiveBufferSize]; //empty the buffer
        }
        catch(Exception e)
        {
          Trace.TraceInformation(string.Format("Exception -> {0}", e.Message));
          driver.SendBuffer(string.Format("-ERR {0}.", e.Message));
          break;
        }
      }
      return;
    }
  }
}
