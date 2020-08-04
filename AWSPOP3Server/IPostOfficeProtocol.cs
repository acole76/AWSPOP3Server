using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;

namespace POP3DotNet
{
	public interface IPostOfficeProtocol
	{
		public TcpClient client { get; set; }
		public NetworkStream stream { get; set; }
		public void Quit();
		public void User(string data);
		public void Pass(string data);
		public void Stat(string data);
		public void List(string data);
		public void Retr(string data);
		public void Dele(string data);
		public void Rset(string data);
		public void Noop(string data);
		public void Top(string data);
		public void Uidl(string data);
		public void Apop(string data);
		public void Capa(string data);
		public void SendBuffer(string message);
	}
}
