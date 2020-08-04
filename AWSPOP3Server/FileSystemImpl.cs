using System;
using System.Linq;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace POP3DotNet
{
	class FileSystemImpl : IPostOfficeProtocol
	{
    public TcpClient client { get; set; }
    public NetworkStream stream { get; set; }

    private string emailFolder = "";
    private bool isAuthorized = false;
    private string sessionUser = "";
    private string sessionDomain = "";
    private DataSet mailBox = new DataSet("mailbox");

    public FileSystemImpl(TcpClient _client, Config config)
    {
      client = _client;
      emailFolder = config.Settings["emailFolder"];
      string[] passwordFile = File.ReadAllLines(string.Format("{0}\\passwd.txt", emailFolder));
      Type intType = Int64.MaxValue.GetType();
      Type strType = "".GetType();
      Type boolType = false.GetType();

      DataTable usersDt = new DataTable("users");
      usersDt.Columns.Add("username", strType);
      usersDt.Columns.Add("password", strType);
      usersDt.Columns.Add("size", intType);

      DataTable messagesDt = new DataTable("messages");
      messagesDt.Columns.Add("index", intType);
      messagesDt.Columns.Add("filename", strType);
      messagesDt.Columns.Add("size", intType);
      messagesDt.Columns.Add("deleted", boolType);

      foreach (var line in passwordFile)
      {
        string[] parts = line.Split(":");
        usersDt.Rows.Add(parts[0], parts[1], 0);
      }
      mailBox.Tables.Add(messagesDt);
      mailBox.Tables.Add(usersDt);
    }

    private bool checkLogin(string username, string password)
    {
      EnumerableRowCollection<DataRow> results = from row in mailBox.Tables["users"].AsEnumerable() where row.Field<string>("username") == username select row;

      if(!results.Any())
      {
        return false;
      }

      DataRow dataRow = results.First<DataRow>();

      string storedPassword = dataRow.Field<string>("password");

      SHA512 shaM = new SHA512Managed();
      byte[] saltedPlainText = Encoding.UTF8.GetBytes(string.Format("{0}:{1}", username, password));
      byte[] hashedBytes = shaM.ComputeHash(saltedPlainText);
      string hash = BitConverter.ToString(hashedBytes).Replace("-", "");

      if (storedPassword.ToLower() == hash.ToLower())
      {
        return true;
      }

      return false;
    }

    public void getMailBox()
    {
      //TODO: implement lock per RFC
      string[] files = Directory.GetFiles(Path.Combine(emailFolder, sessionUser));
      int counter = 1;

      foreach (string file in files)
      {
        FileInfo fi = new FileInfo(file);
        mailBox.Tables["messages"].Rows.Add(counter, file, fi.Length, false);
        counter++;
      }
    }

    public void SendBuffer(string message)
    {
      byte[] returnBytes = Encoding.UTF8.GetBytes(message + "\r\n");
      stream.Write(returnBytes, 0, returnBytes.Length);
    }

    public void Quit()
    {
      if(isAuthorized)
      {
        //Enter UPDATE state
        foreach (DataRow row in mailBox.Tables["messages"].Rows)
        {
          if(row.Field<bool>("deleted"))
          {
            File.Delete(row.Field<string>("filename")); //delete files marked for deletion from filesystem
          }
        }
      }

      SendBuffer("+OK bye.");
      stream.Close();
      client.Close();
    }

    public void User(string data)
    {
      if (data.Length == 0)
      {
        SendBuffer("-ERR Provide a username.");
      }

      sessionDomain = data;
      sessionUser = data;
      SendBuffer("+OK");
    }

    public void Pass(string data)
    {
      if(sessionUser.Length == 0)
      {
        SendBuffer("-ERR No username.");
        return;
      }

      isAuthorized = checkLogin(sessionUser, data);

      if (isAuthorized)
      {
        SendBuffer("+OK Mailbox open.");
        getMailBox();
      }
      else
      {
        SendBuffer("-ERR Failed Login.");
      }
    }

    public void Stat(string data)
    {
      if (!isAuthorized)
      {
        SendBuffer("-ERR Not Authorized.");
        return;
      }

      long mailboxSize = 0;
      foreach (DataRow row in mailBox.Tables["messages"].Rows)
      {
        if(!row.Field<bool>("deleted")) //RFC don't count deleted messages toward size
        {
          mailboxSize += row.Field<Int64>("size");
        }
      }

      SendBuffer(string.Format("+OK {0} {1}", mailBox.Tables["messages"].Rows.Count, mailboxSize));
    }

    public void List(string data)
    {
      if (!isAuthorized)
      {
        SendBuffer("-ERR Not Authorized.");
        return;
      }

      SendBuffer("+OK scan listing follows");

      int msgNum = -1;
      if(data.Length > 0)
      {
        int.TryParse(data, out msgNum);
      }

      string response = "";
      bool msgFound = false;
      foreach (DataRow row in mailBox.Tables["messages"].Rows)
      {
        Int64 counter = row.Field<Int64>("index");
        Int64 size = row.Field<Int64>("size");
        if (msgNum == -1 || msgNum == counter)
        {
          response += string.Format("{0} {1}\r\n", counter, size);
          msgFound = true;
        }
        counter++;
      }

      if(msgFound || msgNum == -1)
      {
        response += ".";
        SendBuffer(response);
      }
      else
      {
        SendBuffer("-ERR no such message");
      }
    }

    public void Retr(string data)
    {
      if (!isAuthorized)
      {
        SendBuffer("-ERR Not Authorized.");
        return;
      }

      Int64 msgNum = -1;
      Int64.TryParse(data, out msgNum);

      if(msgNum == -1)
      {
        SendBuffer("-ERR no such message");
        return;
      }

      EnumerableRowCollection<DataRow> results = from messages in mailBox.Tables["messages"].AsEnumerable() where messages.Field<Int64>("index") == msgNum select messages;

      if (!results.Any())
      {
        SendBuffer("-ERR no such message");
        return;
      }

      DataRow dataRow = results.First<DataRow>();
      SendBuffer("+OK");
      string response = "";

      string[] lines = File.ReadAllLines(dataRow.Field<string>("filename"));

      string prev = "";
      foreach (string line in lines)
      {
        if(line == "." && prev == "\r\n")
        {
          continue; //byte stuff per RFC
        }
        response += line + "\r\n";
        prev = line;
      }

      response += "\r\n.";
      SendBuffer(response);

    }

    public void Dele(string data)
    {
      if (!isAuthorized)
      {
        SendBuffer("-ERR Not Authorized.");
        return;
      }

      Int64 msgNum = -1;
      Int64.TryParse(data, out msgNum);

      if (msgNum == -1)
      {
        SendBuffer("-ERR no such message");
        return;
      }

      EnumerableRowCollection<DataRow> results = from messages in mailBox.Tables["messages"].AsEnumerable() where messages.Field<Int64>("index") == msgNum select messages;

      if (!results.Any())
      {
        SendBuffer("-ERR no such message");
        return;
      }

      DataRow dataRow = results.First<DataRow>();
      dataRow["deleted"] = true;
    }

    public void Rset(string data)
    {
      EnumerableRowCollection<DataRow> results = from messages in mailBox.Tables["messages"].AsEnumerable() where messages.Field<bool>("delete") == true select messages;
      
      if (results.Any())
      {
        foreach (DataRow row in results)
        {
          row["deleted"] = false;
        }
      }

      SendBuffer("+OK");
    }

    public void Noop(string data)
    {
      SendBuffer("+OK");
    }

    public void Top(string data)
    {
      if (!isAuthorized)
      {
        SendBuffer("-ERR Not Authorized.");
        return;
      }

      string[] parts = data.Split(" ");

      if(parts.Length != 2)
      {
        SendBuffer("-ERR invalid parameter");
        return;
      }

      Int64 msgNum = -1;
      Int64.TryParse(parts[0], out msgNum);

      Int64 len = -1;
      Int64.TryParse(parts[1], out len);

      if(len == -1)
      {
        SendBuffer("-ERR invalid length");
        return;
      }

      if (msgNum == -1)
      {
        SendBuffer("-ERR no such message");
        return;
      }

      EnumerableRowCollection<DataRow> results = from messages in mailBox.Tables["messages"].AsEnumerable() where messages.Field<Int64>("index") == msgNum select messages;

      if (!results.Any())
      {
        SendBuffer("-ERR no such message");
        return;
      }

      DataRow dataRow = results.First<DataRow>();
      string filename = dataRow.Field<string>("filename");
      string[] lines = File.ReadAllLines(filename);

      string headers = "";
      string body = "";
      bool isBody = false;
      int counter = 0;
      foreach (string line in lines)
      {
        if(isBody)
        {
          if(counter == len)
          {
            break;
          }
          body += line + "\r\n";
          counter++;
        }
        else
        {
          if (line.Length == 0)
          {
            isBody = true;
          }

          headers += line + "\r\n";
        }
      }
      SendBuffer("+OK");
      SendBuffer(headers + body + "\r\n.");
    }

    public void Uidl(string data)
    {
      if (!isAuthorized)
      {
        SendBuffer("-ERR Not Authorized.");
        return;
      }

      SendBuffer("+OK");

      int msgNum = -1;
      if (data.Length > 0)
      {
        int.TryParse(data, out msgNum);
      }

      string response = "";
      bool msgFound = false;
      foreach (DataRow row in mailBox.Tables["messages"].Rows)
      {
        Int64 counter = row.Field<Int64>("index");
        string filename = row.Field<string>("filename");
        if (msgNum == -1 || msgNum == counter)
        {
          response += string.Format("{0} {1}\r\n", counter, Path.GetFileNameWithoutExtension(filename));
          msgFound = true;
        }
        counter++;
      }

      if (msgFound || msgNum == -1)
      {
        response += ".";
        SendBuffer(response);
      }
      else
      {
        SendBuffer("-ERR no such message");
      }
    }

    public void Apop(string data)
    {
      /*
       APOP requires the password to be stored in plain text and therefor isn't worth implementing
       */
      SendBuffer("-ERR not implemented");
    }

    public void Capa(string data)
    {
      string response = "USER\r\n";
      response += "USER\r\n";
      response += "PASS\r\n";
      response += "TOP\r\n";
      response += "LIST\r\n";
      response += "DELE\r\n";
      response += "UIDL\r\n";
      response += "NOOP\r\n";
      response += "RETR\r\n";
      response += "RSET\r\n";
      SendBuffer("+OK Capability list follows");
      SendBuffer(response);
    }
  }
}
