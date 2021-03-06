﻿using System;
using System.Collections.Generic;
using System.Text;
using System.IO;

namespace SxGeoReader
{
	public enum SxGeoInfoType //информация, включаемая в ответ поиска
	{
		OnlyCountry = 0, //только информация о стране
		CountryCity = 1, //О стране и городе
		FullInfo = 2, //Страна, город, регион
	}

	public enum SxGeoMode
	{
		FileMode = 0, //все кроме индексов читается из файла
		MemoryDiapMode = 1, //в память загружаются диапазоны IP
		MemoryAllMode = 2 //в память загружаются диапазоны и справочники
	}

	public enum SxGeoDirType
	{
		Regions = 0,
		Cites = 1,
		Countries = 2
	}

	public enum SxGeoType
	{
		Universal = 0,
		SxGeoCountry = 1,
		SxGeoCity = 2,
		GeoIPCountry = 11,
		GeoIPCity = 12,
		ipgeobase = 21
	}
	public enum SxGeoEncoding
	{
		UTF8 = 0,
		Latin1 = 1,
		CP1251 = 2
	}
	//заголовок БД
	public struct SxGeoHeader
	{
		public string Version;
		public DateTime Timestamp;
		public SxGeoType DBType;
		public SxGeoEncoding DBEncoding;
		public byte fbIndexLen; //элементов в индексе первых байт (b_idx_len)
		public ushort mIndexLen; //элементов в основном индексе (m_idx_len)
		public ushort Range; //Блоков в одном элементе индекса (range)
		public uint DiapCount; //Количество диапазонов (db_items)
		public byte IdLen; //Размер ID-блока в байтах (1 для стран, 3 для городов) (id_len)
		public ushort MaxRegion;//Максимальный размер записи региона - до 64 кб (max_region)
		public ushort MaxCity;// Максимальный размер записи города - до 64 кб (max_city)
		public uint RegionSize;//Размер справочника регионов (region_size)
		public uint CitySize;//Размер справочника городов (city_size)
		public ushort MaxCountry;//Максимальный размер записи страны - до 64 кб (max_country)
		public uint CountrySize;//Размер справочника стран (country_size)
		public ushort PackSize; //Размер описания формата упаковки города/региона/страны (pack_size)
		public string PackFormat; //описание формата упаковки города/региона/страны

		public uint block_len; //Длина одного блока диапазонов

		//смещения частей БД
		public uint fb_begin; //Начало индекса первых байт
		public uint midx_begin; //начало основного индекса
		public uint db_begin; //начало диапазонов
		public long regions_begin; //начало справочника регионов
		public long cites_begin; //начало справочника городов
		public long countries_begin; //начало справочника стран

		public string pack_country;
		public string pack_city;
		public string pack_region;

		/* справочники идут:
		* 1 - регион
		* 2 - страна
		* 3 - город
		* описание упаковки идет:         
		* страна (точно 1, у региона нет ISO-кода)
		* регион (точно 2, у него координат нет)
		* город
		*/
	}

	public class SxGeoDB
	{

		//переменные, относящиеся к работе класса
		private string FileName = ""; //путь к БД
		private FileStream SxStream = null; //поток для чтения БД
		public bool RevBO { get; set; } //надо ли менять порядок байт
		public long FileSize { get; private set; } //размер файла БД

		//режим памяти
		public SxGeoMode DatabaseMode { get; set; }

		//переменные БД
		private SxGeoHeader Header = new SxGeoHeader(); //заголовок

		//индексы и диапазоны
		private uint[] fb_idx_arr = null; //индекс первых байт
		private uint[] m_idx_arr = null; //основной индекс
		private byte[] db_b = null; //база данных (диапазоны) в виде посл. байт

		//справочники (используются в режиме MemoryAll)
		private byte[] regions_db = null;
		private byte[] cities_db = null;

		//Переменные для сохранения результатов поиска
		private Dictionary<string, object> IPInfo = null;
		private Dictionary<string, Type> IPInfoTypes = null;

		//названия полей, которые не нужны в ответе
		private string[] ignore_fields = new string[] { "country_seek", "id", "region_seek", "country_id" };
		private string[] ignore_fields_ru = new string[] { "name_ru" };
		public bool RemoveRU { get; set; } //если true - из ответа удаляются поля *_ru

		public SxGeoDB(string DBPath)
		{
			FileName = DBPath;
			RevBO = BitConverter.IsLittleEndian;
			DatabaseMode = SxGeoMode.FileMode;
			RemoveRU = false;
		}

		/// <summary>
		/// открыт ли файл
		/// </summary>
		private bool IsOpen
		{
			get { return SxStream != null; }
		}

		//закрытие базы данных
		public void CloseDB()
		{
			SxStream?.Close();
			SxStream = null;
		}

		//Открывает базу данных, проверяет корректность заголовка,
		//вытаскивает данные из заголовка, загружает индексы и диапазоны IP
		public void OpenDB()
		{
			if (SxStream != null)
				throw new InvalidOperationException();

			FileInfo fi = new FileInfo(FileName);
			FileSize = fi.Length;
			if (FileSize < 40)
				throw new InvalidDataException("Bad SxGeo file");

			FileStream sxStream = null;
			try
			{
				sxStream = File.Open(FileName, FileMode.Open, FileAccess.Read, FileShare.Read);

				//проверка сигнатуры ('SxG')
				string sgn = BytesToString(ReadBytes(sxStream, 3));
				if (sgn != "SxG")
					throw new InvalidDataException("Bad signature");

				//версия файла
				Header.Version = GetVersion((byte)sxStream.ReadByte());

				//чтение timestamp
				uint tstamp = ReadUInt(sxStream, RevBO);
				Header.Timestamp = UnixTimeToDateTime(tstamp);

				//тип базы
				Header.DBType = (SxGeoType)sxStream.ReadByte();

				//кодировка 
				Header.DBEncoding = (SxGeoEncoding)sxStream.ReadByte();

				//чтение всего остального заголовка
				Header.fbIndexLen = (byte)sxStream.ReadByte(); ////элементов в индексе первых байт (b_idx_len/byte)
				Header.mIndexLen = ReadUShort(sxStream, RevBO); //элементов в основном индексе (m_idx_len/ushort)
				Header.Range = ReadUShort(sxStream, RevBO); //Блоков в одном элементе индекса (range/ushort)
				Header.DiapCount = ReadUInt(sxStream, RevBO); //Количество диапазонов (db_items)
				Header.IdLen = (byte)sxStream.ReadByte(); //Размер ID-блока в байтах (1 для стран, 3 для городов) (id_len)
				Header.MaxRegion = ReadUShort(sxStream, RevBO); //Максимальный размер записи региона - до 64 кб (max_region)
				Header.MaxCity = ReadUShort(sxStream, RevBO); // Максимальный размер записи города - до 64 кб (max_city)
				Header.RegionSize = ReadUInt(sxStream, RevBO); //Размер справочника регионов (region_size)
				Header.CitySize = ReadUInt(sxStream, RevBO); //Размер справочника городов (city_size)
				Header.MaxCountry = ReadUShort(sxStream, RevBO); //Максимальный размер записи страны - до 64 кб (max_country)
				Header.CountrySize = ReadUInt(sxStream, RevBO); //Размер справочника стран (country_size)
				Header.PackSize = ReadUShort(sxStream, RevBO); //Размер описания формата упаковки города/региона/страны (pack_size)*/

				//проверка заголовка
				if (unchecked(Header.fbIndexLen * Header.mIndexLen * Header.Range * Header.DiapCount * tstamp * Header.IdLen) == 0)
					throw new InvalidDataException("Wrong file format");

				//вытаскиваем описание формата упаковки
				if (Header.PackSize != 0)
				{
					byte[] packformat = ReadBytes(sxStream, Header.PackSize);
					Header.PackFormat = BytesToString(packformat);
					//разбираем формат упаковки на составляющие структуры
					string[] pack = Header.PackFormat.Split('\0');
					if (pack.Length > 0) Header.pack_country = pack[0];
					if (pack.Length > 1) Header.pack_region = pack[1];
					if (pack.Length > 2) Header.pack_city = pack[2];
				}
				Header.block_len = 3 + (uint)Header.IdLen; //длина 1 блока диапазонов

				//вытаскиваем индекс первых байт
				fb_idx_arr = new uint[Header.fbIndexLen];
				for (int i = 0; i < Header.fbIndexLen; i++)
				{
					fb_idx_arr[i] = ReadUInt(sxStream, RevBO);
				}
				//вытаскиваем основной индекс
				m_idx_arr = new uint[Header.mIndexLen];
				for (int i = 0; i < Header.mIndexLen; i++)
				{
					m_idx_arr[i] = ReadUInt(sxStream, RevBO);
				}

				//читаем базу диапазонов IP, 
				//если не установлен режим чтения из файла
				if (DatabaseMode != SxGeoMode.FileMode)
				{
					db_b = ReadBytes(sxStream, (int)(Header.DiapCount * Header.block_len));
				}

				//загружаем справочники в память
				if (DatabaseMode == SxGeoMode.MemoryAllMode)
				{
					//регионы
					if (Header.RegionSize > 0)
					{
						regions_db = ReadBytes(sxStream, (int)Header.RegionSize);
					}

					//города (справочник стран совмещен со справочником городов)
					if (Header.CitySize > 0)
					{
						cities_db = ReadBytes(sxStream, (int)Header.CitySize);
					}

				}

				//Начало индекса первых байт
				Header.fb_begin = 40 + (uint)Header.PackSize;
				//начало основного индекса
				Header.midx_begin = Header.fb_begin + (uint)Header.fbIndexLen * 4;
				//начало диапазонов
				Header.db_begin = Header.midx_begin + (uint)Header.mIndexLen * 4;
				//начало справочника регионов
				Header.regions_begin = Header.db_begin + Header.DiapCount *
					Header.block_len;
				//начало справочника стран
				Header.countries_begin = Header.regions_begin + Header.RegionSize;
				//начало справочника городов
				Header.cites_begin = Header.countries_begin + Header.CountrySize;
			}
			catch
			{
				sxStream?.Close();
				sxStream = null;
				throw;
			}
			finally
			{
				SxStream = sxStream;
			}
		}

		public SxGeoHeader GetHeader()
		{
			return Header;
		}

		public Dictionary<string, Type> GetIPInfoTypes()
		{
			return IPInfoTypes;
		}

		public Dictionary<string, object> GetIPInfo(string ip, SxGeoInfoType infoType)
		{
			if (!IsOpen)
				throw new InvalidOperationException("Database not open.");

			if (ip is null)
				throw new ArgumentNullException(nameof(ip));

			if (!IPConverter.IsIP(ip)) // проверяем IPv4 ли это)
				throw new ArgumentOutOfRangeException(ip + " is not valid IP address.");

			//получаем ID IP-адреса
			uint id = SearchID(ip);
			if (id == 0) // не нашли
				throw new KeyNotFoundException("Not found.");

			//создаем переменные для хранения ответа
			IPInfo = new Dictionary<string, object>();
			IPInfoTypes = new Dictionary<string, Type>();
			//добавляем сам адрес
			IPInfo.Add("ip", ip);
			IPInfoTypes.Add("ip", typeof(string));

			if (Header.IdLen == 1) // БД SxGeo, ничего кроме ISO-кода вывести не сможем
			{
				IPInfo.Add("country_iso", IdToIso(id));
				IPInfoTypes.Add("country_iso", typeof(string));
				return IPInfo;
			}

			// БД SxGeoCountry, можем вывести много чего

			byte[] buf;
			SxGeoUnpack unpacker;
			Dictionary<string, object> data_country;

			// если найденный 'ID' < размера справочника городов
			// город не найден - только страна
			if (id < Header.CountrySize)
			{
				unpacker = new SxGeoUnpack(Header.pack_country, Header.DBEncoding);
				buf = ReadDBDirs(Header.countries_begin, id, Header.MaxCountry, cities_db);
				data_country = unpacker.Unpack(buf);
				AddData(data_country, unpacker.GetRecordTypes(), "country_");
				return IPInfo;
			}

			// город найден, находим и распаковываем информацию о городе
			unpacker = new SxGeoUnpack(Header.pack_city, Header.DBEncoding);
			buf = ReadDBDirs(Header.countries_begin, id, Header.MaxCity, cities_db);
			Dictionary<string, object>  data_city = unpacker.Unpack(buf);

			// о стране по ID страны
			data_country = GetCountry((byte)data_city["country_id"]);

			switch (infoType)
			{
				case SxGeoInfoType.OnlyCountry: // только информация о стране
					AddData(data_country, SxGeoUnpack.GetRecordTypes(Header.pack_country), "country_");
					break;

				case SxGeoInfoType.CountryCity: // страна+город
					AddData(data_country, SxGeoUnpack.GetRecordTypes(Header.pack_country), "country_");
					AddData(data_city, unpacker.GetRecordTypes(), "city_");
					break;

				default: // полная информация с регионом (если есть)
					unpacker = new SxGeoUnpack(Header.pack_region, Header.DBEncoding);
					buf = ReadDBDirs(Header.regions_begin, (uint)data_city["region_seek"], Header.MaxRegion, regions_db);
					Dictionary<string, object> data_region = unpacker.Unpack(buf);
					AddData(data_country, SxGeoUnpack.GetRecordTypes(Header.pack_country), "country_");
					AddData(data_city, SxGeoUnpack.GetRecordTypes(Header.pack_city), "city_");
					AddData(data_region, unpacker.GetRecordTypes(), "region_");
					break;
			}

			return IPInfo;
		}

		private void AddData(Dictionary<string, object> data, Dictionary<string, Type> dataTypes, string prefix)
		{
			// удаляем лишние поля из ответа
			foreach (string remove in ignore_fields)
			{
				data.Remove(remove);
			}
			if (RemoveRU)
			{
				foreach (string remove in ignore_fields_ru)
				{
					data.Remove(remove);
				}
			}

			// добавляем данные в выходной словарь
			foreach (string key in data.Keys)
			{
				IPInfo.Add(prefix + key, data[key]);
				IPInfoTypes.Add(prefix + key, dataTypes[key]);
			}
		}

		// читает данные из справочников
		private byte[] ReadDBDirs(long start, uint seek, uint max, byte[] db)
		{
			byte[] buf;
			if (DatabaseMode == SxGeoMode.MemoryAllMode) //вся БД в памяти
			{
				//в db - массив байт с нужной базой
				buf = bSubstr(db, seek, max);
			}
			else //справочники на диске
			{
				buf = new byte[max];
				SxStream.Seek(start + seek, SeekOrigin.Begin);
				SxStream.Read(buf, 0, (int)max);
			}

			return buf;
		}

		// Поиск информации о стране по ID
		// ВНЕЗАПНО это в оригинале не работало
		// так что свой велосипед
		private Dictionary<string, object> GetCountry(byte CountryID)
		{
			if (DatabaseMode == SxGeoMode.MemoryAllMode)
			{
				return GetCountryMem(CountryID);
			}
			else
			{
				return GetCountryDisk(CountryID);
			}
		}

		// Поиск информации о стране по ID в памяти
		private Dictionary<string, object> GetCountryMem(byte CountryID)
		{
			uint Readed = 0;
			uint NextRead = Header.CountrySize;
			SxGeoUnpack Unpacker = new SxGeoUnpack(Header.pack_country, Header.DBEncoding);

			while (Readed < Header.CountrySize - 1)
			{
				// читаем запись
				byte[] buf = bSubstr(cities_db, Readed, NextRead);

				// распаковываем запись
				int RealLength = 0;
				Dictionary<string, object> Record = Unpacker.Unpack(buf, out RealLength);

				// проверяем, не нашли ли запись
				if ((byte)Record["id"] == CountryID)
				{
					return Record;
				}

				// Сохраняем количество фактических байт записи
				Readed += (uint)RealLength;
			}

			return null;
		}

		// Поиск информации о стране по ID на диске        
		private Dictionary<string, object> GetCountryDisk(byte CountryID)
		{
			// становимся на начало таблицы со странами
			long tableStart = Header.countries_begin;
			Seek(tableStart, SeekOrigin.Begin);

			long Readed = 0;
			int NextRead = (int)Header.CountrySize;
			SxGeoUnpack Unpacker = new SxGeoUnpack(Header.pack_country, Header.DBEncoding);

			while (Readed < Header.CountrySize - 1)
			{
				//читаем запись
				byte[] buf = ReadBytes(NextRead);
				if (buf == null)
				{
					return null;
				}

				//распаковываем запись
				int RealLength;
				Dictionary<string, object> Record = Unpacker.Unpack(buf, out RealLength);

				//проверяем, не нашли ли запись
				if ((byte)Record["id"] == CountryID)
				{
					return Record;
				}

				//Сохраняем количество фактических байт записи
				Readed += RealLength;
				//Отступаем в потоке назад
				long backstep;
				if (tableStart + Readed + Header.MaxCountry > FileSize)
				{
					// если на чтение последних записей файла не хватило
					// максимальной длины записи
					backstep = -NextRead + RealLength;
					NextRead = (int)(FileSize - tableStart - Readed);
					//break;
				}
				else
				{
					backstep = -NextRead + RealLength;
				}

				Seek(backstep, SeekOrigin.Current);
			}
			return null;
		}

		private string IdToIso(uint ID)
		{
			string[] id2iso = new string[] {
			"", "AP", "EU", "AD", "AE", "AF", "AG", "AI", "AL", "AM", "CW", "AO", "AQ", "AR", "AS", "AT", "AU",
			"AW", "AZ", "BA", "BB", "BD", "BE", "BF", "BG", "BH", "BI", "BJ", "BM", "BN", "BO", "BR", "BS",
			"BT", "BV", "BW", "BY", "BZ", "CA", "CC", "CD", "CF", "CG", "CH", "CI", "CK", "CL", "CM", "CN",
			"CO", "CR", "CU", "CV", "CX", "CY", "CZ", "DE", "DJ", "DK", "DM", "DO", "DZ", "EC", "EE", "EG",
			"EH", "ER", "ES", "ET", "FI", "FJ", "FK", "FM", "FO", "FR", "SX", "GA", "GB", "GD", "GE", "GF",
			"GH", "GI", "GL", "GM", "GN", "GP", "GQ", "GR", "GS", "GT", "GU", "GW", "GY", "HK", "HM", "HN",
			"HR", "HT", "HU", "ID", "IE", "IL", "IN", "IO", "IQ", "IR", "IS", "IT", "JM", "JO", "JP", "KE",
			"KG", "KH", "KI", "KM", "KN", "KP", "KR", "KW", "KY", "KZ", "LA", "LB", "LC", "LI", "LK", "LR",
			"LS", "LT", "LU", "LV", "LY", "MA", "MC", "MD", "MG", "MH", "MK", "ML", "MM", "MN", "MO", "MP",
			"MQ", "MR", "MS", "MT", "MU", "MV", "MW", "MX", "MY", "MZ", "NA", "NC", "NE", "NF", "NG", "NI",
			"NL", "NO", "NP", "NR", "NU", "NZ", "OM", "PA", "PE", "PF", "PG", "PH", "PK", "PL", "PM", "PN",
			"PR", "PS", "PT", "PW", "PY", "QA", "RE", "RO", "RU", "RW", "SA", "SB", "SC", "SD", "SE", "SG",
			"SH", "SI", "SJ", "SK", "SL", "SM", "SN", "SO", "SR", "ST", "SV", "SY", "SZ", "TC", "TD", "TF",
			"TG", "TH", "TJ", "TK", "TM", "TN", "TO", "TL", "TR", "TT", "TV", "TW", "TZ", "UA", "UG", "UM",
			"US", "UY", "UZ", "VA", "VC", "VE", "VG", "VI", "VN", "VU", "WF", "WS", "YE", "YT", "RS", "ZA",
			"ZM", "ME", "ZW", "A1", "XK", "O1", "AX", "GG", "IM", "JE", "BL", "MF", "BQ", "SS"
			};

			if (ID >= id2iso.Length) return string.Empty;

			return id2iso[ID];
		}

		#region find_id
		//Поиск ID для IP
		private uint SearchID(string IP)
		{
			//преобразуем IP-адрес в беззнаковый UInt32
			uint ipn = IPConverter.IPToUInt32(IP);
			//получаем 1-й байт IP-адреса
			byte ip1n = (byte)(ipn / 0x1000000);
			//небольшая проверка
			if (ip1n == 0 || ip1n == 10 || ip1n == 127 ||
				ip1n >= Header.fbIndexLen)
				return 0;

			//достаем 3 младших байта            
			uint ipn3b = (uint)(ipn - ip1n * 0x1000000);

			//находим блок данных в индексе первых байт
			uint blocks_min = fb_idx_arr[ip1n - 1];
			uint blocks_max = fb_idx_arr[ip1n];
			uint min = 0; uint max = 0;

			//если длина блока > кол-ва эл-тов в основном индексе
			if (blocks_max - blocks_min > Header.Range)
			{
				//ищем блок данных в основном индексе
				//При целочисленном делении результат 
				//всегда округляется по направлению к нулю 
				//Floor из оригинального исходника не нужен
				uint part = SearchIdx(ipn, blocks_min / Header.Range,
								(blocks_max / Header.Range) - 1);

				// Нашли номер блока в котором нужно искать IP, 
				// теперь находим нужный блок в БД
				min = part > 0 ? part * Header.Range : 0;
				max = part > Header.mIndexLen ? Header.DiapCount : (part + 1) * Header.Range;

				// Нужно проверить чтобы блок не выходил за 
				// пределы блока первого байта
				if (min < blocks_min) min = blocks_min;
				if (max > blocks_max) max = blocks_max;
			}
			else
			{
				min = blocks_min;
				max = blocks_max;
			}
			uint len = max - min;

			uint ID = 0;
			//поиск в БД диапазонов
			if (DatabaseMode != SxGeoMode.FileMode) //БД диапазонов в памяти
			{
				ID = SearchDB(db_b, ipn3b, min, max);
			}
			else //БД диапазонов на диске
			{
				byte[] db_part = LoadDBPart(min, len);
				ID = SearchDB(db_part, ipn3b, 0, len);
			}

			return ID;
		}

		private uint SearchDB(byte[] db, uint ipn, uint min, uint max)
		{
			if (max - min > 1)
			{
				while (max - min > 8)
				{
					uint offset = (min + max) >> 1;
					uint x = getUintFrom3b(bSubstr(db, offset * Header.block_len, 3));
					if (ipn > getUintFrom3b(bSubstr(db, offset * Header.block_len, 3)))
						min = offset;
					else
						max = offset;
				}

				while (ipn >= getUintFrom3b(bSubstr(db, min * Header.block_len, 3))
					&& ++min < max) { }
			}
			else
			{
				min++;
			}

			uint ans = 0;

			if (Header.IdLen == 3) //БД с городами
			{
				ans = getUintFrom3b(
					bSubstr(db, min * Header.block_len - Header.IdLen, Header.IdLen));
			}
			else //только ID стран
			{
				ans = bSubstr(db, min * Header.block_len - Header.IdLen, Header.IdLen)[0];
			}

			return ans;
		}


		//ищет блок данных в основном индексе
		private uint SearchIdx(uint ipn, uint min, uint max)
		{
			while (max - min > 8)
			{
				uint offset = (min + max) >> 1;
				if (ipn > m_idx_arr[offset])
					min = offset;
				else
					max = offset;
			}

			while (ipn > m_idx_arr[min] && min++ < max) { }

			return min;
		}
		#endregion

		#region search_helpers
		//простой аналог substr для массива байт
		private byte[] bSubstr(byte[] Source, uint StartIndex, uint Length)
		{
			byte[] Dest = new byte[Length];
			Array.Copy(Source, StartIndex, Dest, 0, Length);
			return Dest;
		}

		//делает uint из 3 байтов
		//порядок входного массива BigEndian
		private uint getUintFrom3b(byte[] bytes)
		{
			byte[] buf = new byte[4];
			if (RevBO)
			{
				Array.Copy(bytes, 0, buf, 1, 3);
				Array.Reverse(buf);
			}
			else
			{
				Array.Copy(bytes, 0, buf, 0, 3);
			}
			return BitConverter.ToUInt32(buf, 0);
		}


		private byte[] LoadDBPart(uint min, uint len)
		{
			// перемещаемся на начало диапазонов + 
			// найденное минимальное значение
			SxStream.Seek(Header.db_begin + min * Header.block_len, SeekOrigin.Begin);
			return ReadBytes(SxStream, (int)(len * Header.block_len));
		}
		#endregion

		#region file_read_func
		// ползалка по файлу БД
		public void Seek(long Offset, SeekOrigin Origin)
		{
			if (!IsOpen)
				throw new InvalidOperationException("Database not open");

			SxStream.Seek(Offset, Origin);
		}

		public byte[] ReadBytes(int Count)
		{
			if (!IsOpen)
				throw new InvalidOperationException("Database not open");
			return ReadBytes(SxStream, Count);
		}

		private byte[] ReadBytes(FileStream FST, int Count)
		{
			byte[] buf = new byte[Count];
			int readedBytes = FST.Read(buf, 0, Count);
			if (readedBytes != Count)
				throw new FormatException();
			return buf;
		}
		#endregion

		#region header_reading_func
		private ushort ReadUShort(FileStream FST, bool revers)
		{
			byte[] buf = ReadBytes(FST, 2);
			if (buf == null) return 0;
			if (revers) Array.Reverse(buf);
			return BitConverter.ToUInt16(buf, 0);
		}

		private uint ReadUInt(FileStream FST, bool revers)
		{
			byte[] buf = ReadBytes(FST, 4);
			if (buf == null) return 0;
			if (revers) Array.Reverse(buf);
			return BitConverter.ToUInt32(buf, 0);
		}

		private string BytesToString(byte[] bytes)
		{
			//Преобразует массив байт в однобайтовую строку
			// (1251, но для данных целей кодировка не важна)
			if (bytes == null) return null;
			return Encoding.GetEncoding(1251).GetString(bytes);
		}

		private DateTime UnixTimeToDateTime(ulong UnixTime)
		{
			return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(UnixTime);
		}

		private string GetVersion(byte ver)
		{
			string v = ver.ToString();
			if (ver < 10) return v;
			v = v.Insert(v.Length - 1, ".");
			return v;
		}
		#endregion

	}
}
