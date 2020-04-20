using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BitTorent 

{
    //https://stackoverflow.com/questions/4015602/equivalent-of-stringbuilder-for-byte-arrays/4015634#4015634
    public static class MemoryStreamExtensions
    {
        public static void Append(this MemoryStream stream, byte value)
        {
            stream.WriteByte(value);
        }

        public static void Append(this MemoryStream stream, byte[] values)
        {
            stream.Write(values, 0, values.Length);
        }
    }
    public static class BenCoding
    {
        private static readonly byte DictionaryStart = Encoding.UTF8.GetBytes("d")[0];
        private static readonly byte DictionaryEnd   = Encoding.UTF8.GetBytes("e")[0];
        private static readonly byte ListStart       = Encoding.UTF8.GetBytes("l")[0];
        private static readonly byte ListEnd         = Encoding.UTF8.GetBytes("e")[0];
        private static readonly byte NumberStart     = Encoding.UTF8.GetBytes("i")[0];
        private static readonly byte NumberEnd       = Encoding.UTF8.GetBytes("e")[0];
        private static readonly byte ByteArrayDivider= Encoding.UTF8.GetBytes(":")[0];

        public static object Decode(byte[] bytes)
        {
            IEnumerator<byte> enumerator = ((IEnumerable<byte>) bytes).GetEnumerator();
            enumerator.MoveNext();

            return DecodeNextObject(enumerator);
        }

        private static object DecodeNextObject(IEnumerator<byte> enumerator)
        {
            if (enumerator.Current == DictionaryStart)
                return DecodeDictionary(enumerator);
            if (enumerator.Current == ListStart)
                return DecodeList(enumerator);
            if (enumerator.Current == NumberStart)
                return DecodeNumber(enumerator);

            return DecodeByteArray(enumerator);

        }

        public static object DecodeFile(string path)
        {
            if (!File.Exists(path))
                throw  new FileNotFoundException("unable to find file: " +path);
            byte[] bytes = File.ReadAllBytes(path);

            return BenCoding.Decode(bytes);
        }

        private static long DecodeNumber(IEnumerator enumerator)
        {
            List<byte> bytes = new List<byte>();
            //keep pulling bytes until we hit the end flag

            while (enumerator.MoveNext())
            {
                if (enumerator.Current ==  (object) NumberEnd)
                    break;

                if (enumerator.Current != null) bytes.Add((byte) enumerator.Current);
            }

            string numAsString = Encoding.UTF8.GetString((bytes.ToArray()));
            return  Int64.Parse(numAsString);

        }

        private static byte[] DecodeByteArray(IEnumerator<byte> enumerator)
        {
            List<byte> lengthBytes = new List<byte>();

            do
            {
                if (enumerator.Current == ByteArrayDivider)
                {
                    break;
                    
                }
                lengthBytes.Add(enumerator.Current);
            } while (enumerator.MoveNext());

            string lengthString = Encoding.UTF8.GetString(lengthBytes.ToArray());

            if (!Int32.TryParse(lengthString,out var length))
            {
              throw new Exception("unable to parse length of byte array");  
            }
            
            //now read in the actual byte array
            
            byte[] bytes = new byte[length];

            for (int i = 0; i < length; i++)
            {
                enumerator.MoveNext();
                bytes[i] = enumerator.Current;
            }

            return bytes;
        }

        private static List<object> DecodeList(IEnumerator<byte> enumerator)
        {
            List<object> list = new List<object>();
            while (enumerator.MoveNext())
            {
                if (enumerator.Current==ListEnd)
                {
                    break;
                    
                }
                list.Add(DecodeNextObject(enumerator));
            }

            return list;
        }

        private static Dictionary<string, object> DecodeDictionary(IEnumerator<byte> enumerator)
        {
            Dictionary<string,object> dict = new Dictionary<string, object>();
            
            List<string> keys = new List<string>();
            
            //keep decoding objects until we hit the end flag

            while (enumerator.MoveNext())
            {
                if (enumerator.Current == DictionaryEnd)
                    break;
                
                //all keys are valid UTF8 string

                string key = Encoding.UTF8.GetString(DecodeByteArray(enumerator));
                enumerator.MoveNext();
                object val = DecodeNextObject(enumerator);
                
                keys.Add(key);
                dict.Add(key,val);
            }
            
            //verify incoming dict is sorted correctly
            //we will not be able to create an identical encoding otherwise

            var sortedKeys = keys.OrderBy(x => BitConverter.ToString(Encoding.UTF8.GetBytes(x)));
            if (!keys.SequenceEqual(sortedKeys))
            {
                throw  new Exception("Error loading dictionary:Keys not sorted");
            }

            return dict;
            
                
        }

        public static byte[] Encode(object obj)
        {
            MemoryStream buffer = new MemoryStream();//generate our byte array
            EncodeNextObject(buffer, obj);
            return buffer.ToArray();
        }

        public static void EncodeToFile(object obj, string path)
        {
            File.WriteAllBytes(path,Encode(obj));
        }

        private static void EncodeNextObject(MemoryStream buffer, object obj)
        {
            if (obj is byte[] bytes)
            {
                EncodeByteArray(buffer,bytes);
            }
            else if (obj is string s)
            {
                EncodeString(buffer, s);
            }
            else if (obj is long l)
            {
                EncodeNumber(buffer, l);
            }
            else if (obj.GetType() == typeof(List<object>))
            {
                EncodeList(buffer, (List<object>) obj);
            }
            else if (obj.GetType() == typeof(Dictionary<string, object>))
            {
                EncodeDictionary(buffer, (Dictionary<string, object>) obj);
            }
            else
            {
                throw new Exception("unable to encode type " + obj.GetType());
            }
        }
        private static void EncodeNumber(MemoryStream buffer, long input)
        {
            buffer.Append(NumberStart);
            buffer.Append(Encoding.UTF8.GetBytes(Convert.ToString(input)));
            buffer.Append(NumberEnd);
        }

        private static void EncodeByteArray(MemoryStream buffer, byte[] body)
        {
            buffer.Append(Encoding.UTF8.GetBytes(Convert.ToString(body.Length)));
            buffer.Append(ByteArrayDivider);
            buffer.Append(body);
        }

        private static void EncodeString(MemoryStream buffer, string input)
        {
            EncodeByteArray(buffer,Encoding.UTF8.GetBytes(input));
        }

        private static void EncodeList(MemoryStream buffer, List<object> input)
        {
            buffer.Append(ListStart);
            foreach (var item in input)
            {
                EncodeNextObject();
            }

            buffer.Append(ListEnd);
        }

        private static void EncodeDictionary(MemoryStream buffer, Dictionary<string, object> input)
        {
            buffer.Append(DictionaryStart);
            //Sort dict by their bytes 
            var sortedKeys = input.Keys.ToList().OrderBy(x => BitConverter.ToString(Encoding.UTF8.GetBytes(x)));

            foreach (var key in sortedKeys)
            {
                EncodeString(buffer,key);
                EncodeNextObject(buffer,input[key]);
            }

            buffer.Append(DictionaryEnd);
        }

        private static void EncodeNextObject()
        {
            throw new NotImplementedException();
        }
    }
}