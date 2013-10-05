﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using MonoTorrent.Client;
using MonoTorrent.Client.Encryption;
using MonoTorrent.Common;
using MonoTorrent.Dht;
using MonoTorrent.Dht.Listeners;
using Mygod.Xml.Linq;
using SevenZip;

// ReSharper disable ImplicitlyCapturedClosure

namespace Mygod.Skylark.BackgroundRunner
{
    public static class Program
    {
        private static void Main()
        {
            try
            {
                Console.WriteLine("恭喜你得到了这款无聊的程序！随便打点东西吧。");
                switch (Console.ReadLine().ToLowerInvariant())
                {
                    case "offline-download":
                        OfflineDownload(Console.ReadLine(), Console.ReadLine());
                        break;
                    case "ftp-upload":
                        FtpUpload(Console.ReadLine());
                        break;
                    case "decompress":
                        Decompress(Console.ReadLine());
                        break;
                    case "compress":
                        Compress(Console.ReadLine());
                        break;
                    case "convert":
                        Convert(Console.ReadLine());
                        break;
                    case "cross-app-copy":
                        CrossAppCopy(Console.ReadLine());
                        break;
                    case "bit-torrent":
                        BitTorrent(Console.ReadLine());
                        break;
                    default:
                        Console.WriteLine("无法识别。");
                        break;
                }
            }
            catch (Exception exc)
            {
                File.AppendAllText(@"Data\error.log", string.Format("[{0}] {1}{2}{2}", DateTime.UtcNow, exc.GetMessage(),
                                                                    Environment.NewLine));
            }
        }

        private static string GetFilePath(string path)
        {
            if (path.Contains("%", StringComparison.InvariantCultureIgnoreCase)
                || path.Contains("#", StringComparison.InvariantCultureIgnoreCase)) throw new FormatException();
            return Path.Combine("Files", path);
        }
        private static string GetDataPath(string path)
        {
            if (path.Contains("%", StringComparison.InvariantCultureIgnoreCase)
                || path.Contains("#", StringComparison.InvariantCultureIgnoreCase)) throw new FormatException();
            return Path.Combine("Data", path);
        }
        private static string GetDataFilePath(string path)
        {
            return GetDataPath(path) + ".data";
        }

        private static readonly Regex AccountRemover = new Regex(@"^ftp:\/\/[^\/]*?:[^\/]*?@", RegexOptions.Compiled);
        private static string GetFileName(string url)
        {
            url = url.TrimEnd('/', '\\');
            int i = url.IndexOf('?'), j = url.IndexOf('#');
            if (j >= 0 && (i < 0 || i > j)) i = j;
            if (i >= 0) url = url.Substring(0, i);
            return Path.GetFileName(url);
        }
        private static void OfflineDownload(string url, string path)
        {
            FileStream fileStream = null;
            XDocument doc = null;
            XElement root = null;
            string xmlPath = null;
            try
            {
                var request = WebRequest.Create(url);
                request.Timeout = Timeout.Infinite;
                var response = request.GetResponse();
                var stream = response.GetResponseStream();
                var disposition = response.Headers["Content-Disposition"] ?? string.Empty;
                var pos = disposition.IndexOf("filename=", StringComparison.Ordinal);
                long? fileLength;
                if (stream.CanSeek) fileLength = stream.Length;
                else
                    try
                    {
                        fileLength = response.ContentLength;
                    }
                    catch
                    {
                        fileLength = null;
                    }
                if (fileLength < 0) fileLength = null;

                var fileName = (pos >= 0 ? disposition.Substring(pos + 9).Trim('"', '\'').UrlDecode() : GetFileName(url)).UrlDecode();
                string mime, extension;
                try
                {
                    mime = Helper.GetMime(response.ContentType);
                    extension = Helper.GetDefaultExtension(mime);
                }
                catch
                {
                    extension = Path.GetExtension(fileName);
                    mime = Helper.GetDefaultExtension(extension);
                }
                if (!string.IsNullOrEmpty(extension) && !fileName.EndsWith(extension, StringComparison.Ordinal)) fileName += extension;

                path = Path.Combine(path, fileName);
                xmlPath = GetDataFilePath(path);
                path = GetFilePath(path);

                doc = new XDocument(root = new XElement("file", new XAttribute("url", AccountRemover.Replace(url, "ftp://")),
                    new XAttribute("state", "downloading"), new XAttribute("pid", Pid), new XAttribute("fileName", fileName),
                    new XAttribute("startTime", DateTime.UtcNow.Ticks), new XAttribute("mime", mime ?? "application/octet-stream")));
                if (fileLength != null) root.SetAttributeValue("size", fileLength);
                doc.Save(xmlPath);

                stream.CopyTo(fileStream = File.Create(path));

                root.SetAttributeValue("endTime", DateTime.UtcNow);
                root.SetAttributeValue("state", "ready");
                doc.Save(xmlPath);
            }
            catch (Exception exc)
            {
                if (doc == null || root == null) throw;
                root.SetAttributeValue("message", exc.Message);
                doc.Save(xmlPath);
            }
            finally
            {
                if (fileStream != null) fileStream.Close();
            }
        }

        private static void FtpUpload(string id)
        {
            var xmlPath = GetDataPath(id + ".ftpUpload.task");
            if (!File.Exists(xmlPath)) return;
            var doc = XHelper.Load(xmlPath);
            var root = doc.Root;
            try
            {
                root.SetAttributeValue("pid", Pid);
                string source = root.GetAttributeValue("source"), target = root.GetAttributeValue("target");
                root.SetAttributeValue("target", AccountRemover.Replace(target, "ftp://"));
                doc.Save(xmlPath);
                var queue = new Queue<string>();
                foreach (var e in root.ElementsCaseInsensitive("directory")) queue.Enqueue(e.Value);
                var files = new List<string>();
                var total = 0L;
                foreach (var file in root.ElementsCaseInsensitive("file").Select(e => e.Value))
                {
                    files.Add(file);
                    total += new FileInfo(GetFilePath(FileHelper.Combine(source, file))).Length;
                }
                while (queue.Count > 0)
                {
                    string item = queue.Dequeue(), itemFile = GetFilePath(FileHelper.Combine(source, item));
                    foreach (var file in Directory.EnumerateFiles(itemFile).Select(file => Path.Combine(item, Path.GetFileName(file))))
                    {
                        files.Add(file);
                        total += new FileInfo(GetFilePath(FileHelper.Combine(source, file))).Length;
                    }
                    foreach (var dir in Directory.EnumerateDirectories(itemFile)) queue.Enqueue(Path.Combine(item, Path.GetFileName(dir)));
                    var request = (FtpWebRequest)WebRequest.Create(Path.Combine(target, item));
                    request.Timeout = Timeout.Infinite;
                    request.UseBinary = true;
                    request.UsePassive = true;
                    request.KeepAlive = true;
                    request.Method = WebRequestMethods.Ftp.MakeDirectory;
                    request.GetResponse().Close();
                }
                foreach (var file in files) FileHelper.WaitForReady(GetDataFilePath(file));
                root.SetAttributeValue("total", total);
                var completed = 0L;
                foreach (var file in files)
                {
                    root.SetAttributeValue("completed", completed);
                    root.SetAttributeValue("current", file);
                    doc.Save(xmlPath);
                    var request = (FtpWebRequest)WebRequest.Create(Path.Combine(target, file));
                    request.Timeout = Timeout.Infinite;
                    request.UseBinary = true;
                    request.UsePassive = true;
                    request.KeepAlive = true;
                    request.Method = WebRequestMethods.Ftp.UploadFile;
                    using (var src = File.OpenRead(GetFilePath(FileHelper.Combine(source, file))))
                    using (var dst = request.GetRequestStream())
                    {
                        var byteBuffer = new byte[1048576];
                        var bytesSent = src.Read(byteBuffer, 0, 1048576);
                        while (bytesSent != 0)
                        {
                            dst.Write(byteBuffer, 0, bytesSent);
                            completed += bytesSent;
                            root.SetAttributeValue("completed", completed);
                            doc.Save(xmlPath);
                            bytesSent = src.Read(byteBuffer, 0, 1048576);
                        }
                    }
                    request.GetResponse().Close();
                }
                var current = root.Attribute("current");
                if (current != null) current.Remove();
                root.SetAttributeValue("state", "ready");
                root.SetAttributeValue("finishTime", DateTime.UtcNow.Ticks);
                doc.Save(xmlPath);
            }
            catch (Exception exc)
            {
                root.SetAttributeValue("message", exc.GetMessage());
                doc.Save(xmlPath);
            }
        }

        private static void Decompress(string id)
        {
            var xmlPath = GetDataPath(id + ".decompress.task");
            if (!File.Exists(xmlPath)) return;
            var doc = XHelper.Load(xmlPath);
            var root = doc.Root;
            SevenZipExtractor extractor = null;
            try
            {
                root.SetAttributeValue("pid", Pid);
                root.SetAttributeValue("progress", 0);
                var progress = 0;
                doc.Save(xmlPath);
                string archive = root.GetAttributeValue("archive"), directory = root.GetAttributeValue("directory").Replace('/', '\\'),
                       filePath = GetFilePath(directory), dataPath = GetDataPath(directory);
                if (!Directory.Exists(filePath)) Directory.CreateDirectory(filePath);
                if (!Directory.Exists(dataPath)) Directory.CreateDirectory(dataPath);
                extractor = new SevenZipExtractor(GetFilePath(archive));
                extractor.FileExtractionStarted += (sender, e) =>
                {
                    FileHelper.WriteAllText(GetDataFilePath(FileHelper.Combine(directory, e.FileInfo.FileName)),
                                            string.Format("<file state=\"decompressing\" id=\"{0}\" pid=\"{1}\" />", id, Pid));
                    root.SetAttributeValue("current", e.FileInfo.FileName);
                    doc.Save(xmlPath);
                };
                extractor.FileExtractionFinished += (sender, e) =>
                {
                    FileHelper.WriteAllText(GetDataFilePath(FileHelper.Combine(directory, e.FileInfo.FileName)),
                        string.Format("<file mime=\"{0}\" state=\"ready\" />", Helper.GetMimeType(e.FileInfo.FileName)));
                    if (e.PercentDone == progress) return;
                    root.SetAttributeValue("progress", progress = e.PercentDone);
                    doc.Save(xmlPath);
                };
                extractor.ExtractArchive(filePath);
                var current = root.Attribute("current");
                if (current != null) current.Remove();
                root.SetAttributeValue("finished", DateTime.UtcNow.Ticks);
                doc.Save(xmlPath);
            }
            catch (SevenZipException)
            {
                if (extractor == null) throw;
                root.SetAttributeValue("message", string.Join("<br />", extractor.Exceptions.Select(e => e.Message)));
                doc.Save(xmlPath);
            }
            catch (Exception exc)
            {
                root.SetAttributeValue("message", exc.Message);
                doc.Save(xmlPath);
            }
        }

        private static void Compress(string path)
        {
            var xmlPath = GetDataFilePath(path);
            var doc = XHelper.Load(xmlPath);
            var root = doc.Root;
            SevenZipCompressor compressor = null;
            try
            {
                root.SetAttributeValue("pid", Pid);
                doc.Save(xmlPath);
                string baseFolder = root.GetAttributeValue("baseFolder"), baseFileFolder = GetFilePath(baseFolder);
                var queue = new Queue<string>();
                foreach (var e in root.ElementsCaseInsensitive("directory")) queue.Enqueue(FileHelper.Combine(baseFolder, e.Value));
                var files = root.ElementsCaseInsensitive("file").Select(e => FileHelper.Combine(baseFolder, e.Value)).ToList();
                while (queue.Count > 0)
                {
                    string item = queue.Dequeue(), itemFile = GetFilePath(item);
                    files.AddRange(Directory.EnumerateFiles(itemFile).Select(file => Path.Combine(item, Path.GetFileName(file))));
                    foreach (var dir in Directory.EnumerateDirectories(itemFile)) queue.Enqueue(Path.Combine(item, Path.GetFileName(dir)));
                }
                foreach (var file in files) FileHelper.WaitForReady(GetDataFilePath(file));
                compressor = new SevenZipCompressor
                    { CompressionLevel = (CompressionLevel)Enum.Parse(typeof(CompressionLevel), root.GetAttributeValue("level"), true) };
                switch (Path.GetExtension(path).ToLowerInvariant())
                {
                    case ".7z":
                        compressor.ArchiveFormat = OutArchiveFormat.SevenZip;
                        break;
                    case ".zip":
                        compressor.ArchiveFormat = OutArchiveFormat.Zip;
                        break;
                    case ".tar":
                        compressor.ArchiveFormat = OutArchiveFormat.Tar;
                        break;
                }
                compressor.FileCompressionStarted += (sender, e) =>
                {
                    root.SetAttributeValue("current", e.FileName);
                    root.SetAttributeValue("progress", e.PercentDone);
                    doc.Save(xmlPath);
                };
                compressor.CompressFiles(GetFilePath(path), Path.GetFullPath(baseFileFolder).Length + 1,
                                         files.Select(file => Path.GetFullPath(GetFilePath(file))).ToArray());
                var current = root.Attribute("current");
                if (current != null) current.Remove();
                root.SetAttributeValue("state", "ready");
                root.SetAttributeValue("finishTime", DateTime.UtcNow.Ticks);
                doc.Save(xmlPath);
            }
            catch (SevenZipException)
            {
                if (compressor == null) throw;
                root.SetAttributeValue("message", string.Join("<br />", compressor.Exceptions.Select(e => e.Message)));
                doc.Save(xmlPath);
            }
            catch (Exception exc)
            {
                root.SetAttributeValue("message", exc.GetMessage());
                doc.Save(xmlPath);
            }
        }

        private static void BitTorrent(string id)
        {
            var xmlPath = GetDataPath(id + ".bitTorrent.task");
            if (!File.Exists(xmlPath)) return;
            var doc = XHelper.Load(xmlPath);
            var root = doc.Root;
            try
            {
                root.SetAttributeValue("pid", Pid);
                root.SetAttributeValue("downloaded", 0);
                string torrentPath = root.GetAttributeValue("torrent"), directory = root.GetAttributeValue("directory").Replace('/', '\\'),
                       filePath = GetFilePath(directory);
                var torrent = Torrent.Load(GetFilePath(torrentPath));
                long length = 0;
                foreach (var file in torrent.Files)
                {
                    length += file.Length;
                    FileHelper.WriteAllText(GetDataFilePath(FileHelper.Combine(directory, file.Path)),
                                            string.Format("<file state=\"downloading-bit-torrent\" id=\"{0}\" pid=\"{1}\" />", id, Pid));
                }
                root.SetAttributeValue("length", length);
                doc.Save(xmlPath);

                var listenedPorts =
                    new HashSet<int>(IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(endPoint => endPoint.Port));
                var port = 10000;
                while (listenedPorts.Contains(port)) port++;
                var engine = new ClientEngine(
                    new EngineSettings(filePath, port) { PreferEncryption = false, AllowedEncryption = EncryptionTypes.All });
                engine.ChangeListenEndpoint(new IPEndPoint(IPAddress.Any, port));
                var listener = new DhtListener(new IPEndPoint(IPAddress.Any, port));
                engine.RegisterDht(new DhtEngine(listener));
                listener.Start();
                engine.DhtEngine.Start();
                if (!Directory.Exists(engine.Settings.SavePath)) Directory.CreateDirectory(engine.Settings.SavePath);
                var manager = new TorrentManager(torrent, filePath, new TorrentSettings(4, 150, 0, 0));
                engine.Register(manager);
                manager.PieceHashed += (sender, e) =>
                {
                    root.SetAttributeValue("downloaded", (long) (e.TorrentManager.Progress * length / 100));
                    try
                    {
                        doc.Save(xmlPath);
                    }
                    catch (IOException) { }
                };
                manager.Start();
                while (!manager.Complete) Thread.Sleep(1000);
                foreach (var file in torrent.Files) FileHelper.WriteAllText(GetDataFilePath(FileHelper.Combine(directory, file.Path)),
                    string.Format("<file mime=\"{0}\" state=\"ready\" />", Helper.GetMimeType(file.Path)));
                root.SetAttributeValue("downloaded", length);
                root.SetAttributeValue("finished", DateTime.UtcNow.Ticks);
                doc.Save(xmlPath);
            }
            catch (Exception exc)
            {
                root.SetAttributeValue("message", exc.Message);
                doc.Save(xmlPath);
            }
        }

        private static readonly Regex TimeParser = new Regex(@"size=(.*)kB time=(.*)bitrate=", RegexOptions.Compiled);
        private static readonly int Pid = Process.GetCurrentProcess().Id;
        private static void Convert(string path)
        {
            var xmlPath = GetDataFilePath(path);
            var doc = XHelper.Load(xmlPath);
            var root = doc.Root;
            try
            {
                root.SetAttributeValue("pid", Pid);
                doc.Save(xmlPath);
                var process = new Process { StartInfo = new ProcessStartInfo("plugins/ffmpeg/ffmpeg.exe",
                    string.Format("-i \"{0}\"{2} \"{1}\" -y", GetFilePath(root.GetAttributeValue("input")), GetFilePath(path), 
                    root.GetAttributeValue("arguments") ?? string.Empty)) { UseShellExecute = false, RedirectStandardError = true } };
                process.Start();
                while (!process.StandardError.EndOfStream)
                {
                    var line = process.StandardError.ReadLine();
                    var match = TimeParser.Match(line);
                    if (!match.Success) continue;
                    try
                    {
                        root.SetAttributeValue("size", long.Parse(match.Groups[1].Value.Trim()) << 10);
                        root.SetAttributeValue("time", TimeSpan.Parse(match.Groups[2].Value.Trim()).Ticks);
                        doc.Save(xmlPath);
                    }
                    catch { }
                }
                root.SetAttributeValue("state", "ready");
                root.SetAttributeValue("finishTime", DateTime.UtcNow.Ticks);
                doc.Save(xmlPath);
            }
            catch (Exception exc)
            {
                root.SetAttributeValue("message", exc.GetMessage());
                doc.Save(xmlPath);
            }
        }

        private static readonly WebClient Client = new WebClient();
        private static bool CopyFile(string domain, string path, string target, XDocument doc, string xmlPath,
                                     ref long fileCopied, ref long sizeCopied, bool logging = true)
        {
            var targetFile = FileHelper.Combine(target, Path.GetFileName(path));
            doc.Root.SetAttributeValue("current", targetFile);
            doc.Save(xmlPath);
            try
            {
                var root = XDocument.Parse(Client.DownloadString(string.Format("http://{0}/Api/Details/{1}", domain, path))).Root;
                if (root.GetAttributeValue("status") != "ok") throw new ExternalException(root.GetAttributeValue("message"));
                OfflineDownload(string.Format("http://{0}/Download/{1}", domain, path), target);
                var file = root.Element("file");
                FileHelper.SetDefaultMime(GetDataFilePath(targetFile), file.GetAttributeValue("mime"));
                fileCopied++;
                sizeCopied += file.GetAttributeValue<long>("size");
                doc.Root.SetAttributeValue("fileCopied", fileCopied);
                doc.Root.SetAttributeValue("sizeCopied", sizeCopied);
                doc.Save(xmlPath);
                return true;
            }
            catch (Exception exc)
            {
                if (logging)
                {
                    doc.Root.SetAttributeValue("message", (doc.Root.GetAttributeValue("message") ?? string.Empty)
                        + string.Format("复制 /{0} 时发生了错误：{2}{1}{2}", targetFile, exc.GetMessage(), Environment.NewLine));
                    doc.Save(xmlPath);
                }
                return false;
            }
        }
        private static void CopyDirectory(string domain, string path, string target, XDocument doc, string xmlPath,
                                          ref long fileCopied, ref long sizeCopied)
        {
            doc.Root.SetAttributeValue("current", FileHelper.Combine(target, Path.GetFileName(path)));
            doc.Save(xmlPath);
            try
            {
                var root = XDocument.Parse(Client.DownloadString(string.Format("http://{0}/Api/List/{1}", domain, path))).Root;
                if (root.GetAttributeValue("status") != "ok") throw new ExternalException(root.GetAttributeValue("message"));
                foreach (var element in root.Elements())
                {
                    var name = element.GetAttributeValue("name");
                    switch (element.Name.LocalName)
                    {
                        case "directory":
                            var dir = FileHelper.Combine(target, name);
                            Directory.CreateDirectory(GetFilePath(dir));
                            Directory.CreateDirectory(GetDataPath(dir));
                            CopyDirectory(domain, FileHelper.Combine(path, name), dir, doc, xmlPath,
                                          ref fileCopied, ref sizeCopied);
                            break;
                        case "file":
                            CopyFile(domain, FileHelper.Combine(path, name), target, doc, xmlPath, ref fileCopied, ref sizeCopied);
                            break;
                    }
                }
            }
            catch (Exception exc)
            {
                doc.Root.SetAttributeValue("message", (doc.Root.GetAttributeValue("message") ?? string.Empty)
                    + string.Format("复制 /{0} 时发生了错误：{2}{1}{2}", target, exc.GetMessage(), Environment.NewLine));
                doc.Save(xmlPath);
            }
        }
        private static void CrossAppCopy(string id)
        {
            var xmlPath = GetDataPath(id + ".crossAppCopy.task");
            if (!File.Exists(xmlPath)) return;
            var doc = XHelper.Load(xmlPath);
            string domain = doc.Root.GetAttributeValue("domain"), path = doc.Root.GetAttributeValue("path"),
                   target = doc.Root.GetAttributeValue("target");
            long fileCopied = 0, sizeCopied = 0;
            doc.Root.SetAttributeValue("pid", Pid);
            doc.Save(xmlPath);
            if (!CopyFile(domain, path, target, doc, xmlPath, ref fileCopied, ref sizeCopied, false))
                CopyDirectory(domain, path, target, doc, xmlPath, ref fileCopied, ref sizeCopied);
            var current = doc.Root.Attribute("current");
            if (current != null) current.Remove();
            doc.Root.SetAttributeValue("finished", DateTime.UtcNow.Ticks);
            doc.Save(xmlPath);
        }
    }
}
