using System;
using System.Net;
using System.Net.Sockets;

namespace SxGeoReader
{
	public static class IPConverter
	{
		public static bool IsIP(string ipString)
		{
			return IPAddress.TryParse(ipString, out IPAddress ip) && ip.AddressFamily == AddressFamily.InterNetwork;
		}

		public static string ToStandForm(string ipString)
		{
			if (IPAddress.TryParse(ipString, out IPAddress ip) ||
				ip.AddressFamily == AddressFamily.InterNetwork)
			{
				return ip.ToString();
			}
			return string.Empty;
		}

		public static int IPToInt32(string IP)
		{
			//получаем байты адреса, они всегда в big-endian
			byte[] addrbytes = IPAddress.Parse(IP).GetAddressBytes();
			int n = BitConverter.ToInt32(addrbytes, 0); //IP в виде Int32 big-endian 
			if (BitConverter.IsLittleEndian) //если в системе little-endian порядок
			{
				n = IPAddress.NetworkToHostOrder(n); //надо перевернуть
			}
			return n;
		}

		public static uint IPToUInt32(string IP)
		{
			IPAddress addr = IPAddress.Parse(IP);
			byte[] addrbytes = addr.GetAddressBytes();
			if (BitConverter.IsLittleEndian)
			{
				Array.Reverse(addrbytes);
			}
			return BitConverter.ToUInt32(addrbytes, 0);
		}


		public static byte[] GetBytesBE(string IP)
		{
			return IPAddress.Parse(IP).GetAddressBytes();
		}

		public static byte[] GetBytesLE(string IP)
		{
			byte[] addrbytes = IPAddress.Parse(IP).GetAddressBytes();
			Array.Reverse(addrbytes);
			return addrbytes;
		}
	}
}
