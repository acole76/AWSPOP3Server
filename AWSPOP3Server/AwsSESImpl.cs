using System;
using System.Linq;
using System.Data;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3.Model;
using Amazon.S3;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;

namespace POP3DotNet
{
	class AwsSESImpl : IPostOfficeProtocol
  {
    public TcpClient client { get; set; }
    public NetworkStream stream { get; set; }

    private bool isAuthorized = false;
    private string sessionUser = "";
    private string sessionDomain = "";
    private DataSet mailBox = new DataSet("mailbox");
    private AmazonS3Client awsClient;
    private string bucketName = "";
    private string bucketPrefix = "";

    public AwsSESImpl(TcpClient _client, Config config)
    {
      client = _client;
      awsClient = new AmazonS3Client(config.Settings["awskey"], config.Settings["awssecret"]);
      bucketName = config.Settings["bucket"];
      bucketPrefix = config.Settings["prefix"];

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

      mailBox.Tables.Add(messagesDt);
      mailBox.Tables.Add(usersDt);
    }

    private string readS3File(string bucket, string key)
    {
      GetObjectRequest readFileRequest = new GetObjectRequest
      {
        BucketName = bucket,
        Key = key
      };

      Task<GetObjectResponse> getObjectResponse = awsClient.GetObjectAsync(readFileRequest);

      GetObjectResponse response = getObjectResponse.Result;
      StreamReader reader = new StreamReader(response.ResponseStream);

      return reader.ReadToEnd();
    }

    private void deleteS3File(string bucket, string key)
    {
      DeleteObjectRequest deleteFileRequest = new DeleteObjectRequest
      {
        BucketName = bucket,
        Key = key
      };

      Task<DeleteObjectResponse> getObjectResponse = awsClient.DeleteObjectAsync(deleteFileRequest);

      DeleteObjectResponse response = getObjectResponse.Result;
      Trace.TraceInformation("Deleting File {0} -> {1}", key, response.HttpStatusCode.ToString());
    }

    private bool checkLogin(string username, string password)
    {
      if (mailBox.Tables["users"].Rows.Count == 0)
      {
        string[] passwordFile = readS3File(bucketName, string.Format("{0}/{1}/{2}", bucketPrefix, sessionDomain, "passwd.txt")).Split("\r\n");
        foreach (var line in passwordFile)
        {
          string[] parts = line.Trim().Split(":");
          mailBox.Tables["users"].Rows.Add(parts[0], parts[1], 0);
        }
      }

      EnumerableRowCollection<DataRow> results = from row in mailBox.Tables["users"].AsEnumerable() where row.Field<string>("username") == username select row;

      if (!results.Any())
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
      // List all objects
      ListObjectsRequest listRequest = new ListObjectsRequest
      {
        BucketName = bucketName,
        Prefix = string.Format("{0}/{1}/{2}/", bucketPrefix, sessionDomain, sessionUser)
      };

      Task<ListObjectsResponse> listResponse = awsClient.ListObjectsAsync(listRequest);

      ListObjectsResponse response = listResponse.Result;

      int counter = 1;
      Int64 mailboxSize = 0;
      foreach (S3Object obj in response.S3Objects)
      {
        if(obj.Size > 0) // if zero length, it's probably a folder
        {
          mailBox.Tables["messages"].Rows.Add(counter, obj.Key, obj.Size, false);
          mailboxSize += obj.Size;
          counter++;
        }
      }

      EnumerableRowCollection<DataRow> results = from users in mailBox.Tables["users"].AsEnumerable() where users.Field<string>("username") == sessionUser select users;

      if (results.Any())
      {
        DataRow dataRow = results.First<DataRow>();
        dataRow["size"] = mailboxSize;
      }
    }

    public void SendBuffer(string message)
    {
      if(client.Connected)
      {
        if (stream.CanWrite)
        {
          byte[] returnBytes = Encoding.UTF8.GetBytes(message + "\r\n");
          stream.Write(returnBytes, 0, returnBytes.Length);
        }
        else
        {
          Trace.TraceInformation("Unabel to write to stream");
        }
      }
    }

    public void Quit()
    {
      if (isAuthorized)
      {
        //Enter UPDATE state
        foreach (DataRow row in mailBox.Tables["messages"].Rows)
        {
          if (row.Field<bool>("deleted"))
          {
            //awsClient.DeleteObjectAsync();
            //File.Delete(row.Field<string>("filename")); //delete files marked for deletion from filesystem
            string filename = row.Field<string>("filename");
            deleteS3File(bucketName, filename);
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
        return;
      }

      string[] user_parts = data.Split("@");

      if(user_parts.Length != 2)
      {
        SendBuffer("-ERR Invalid username. Use user@domain.com");
        return;
      }

      sessionDomain = user_parts[1];
      sessionUser = user_parts[0];
      SendBuffer("+OK");
    }

    public void Pass(string data)
    {
      if (sessionUser.Length == 0)
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
        if (!row.Field<bool>("deleted")) //RFC don't count deleted messages toward size
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
      if (data.Length > 0)
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

    public void Retr(string data)
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
      SendBuffer("+OK");
      string response = "";

      string filename = dataRow.Field<string>("filename");
      string[] lines = readS3File(bucketName, filename).Split("\r\n");

      string prev = "";
      foreach (string line in lines)
      {
        if (line == "." && prev == "\r\n")
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
      SendBuffer("+OK");
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

      if (parts.Length != 2)
      {
        SendBuffer("-ERR invalid parameter");
        return;
      }

      Int64 msgNum = -1;
      Int64.TryParse(parts[0], out msgNum);

      Int64 len = -1;
      Int64.TryParse(parts[1], out len);

      if (len == -1)
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
      string[] lines = readS3File(bucketName, filename).Split("\r\n");
      //string[] lines = File.ReadAllLines(filename);

      string headers = "";
      string body = "";
      bool isBody = false;
      int counter = 0;
      foreach (string line in lines)
      {
        if (isBody)
        {
          if (counter == len)
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
