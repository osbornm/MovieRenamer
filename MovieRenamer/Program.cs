using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.TMDb;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MediaToolkit;
using MediaToolkit.Model;
using TagLib;


namespace MovieRenamer
{
    class Program
    {
        static void Main(string[] args)
        {
            var directory = new DirectoryInfo(ConfigurationManager.AppSettings["from"]);
            var files = Enumerable.Empty<FileInfo>();
            files = files.Concat(directory.GetFiles("*.mp4"));
            files = files.Concat(directory.GetFiles("*.m4v"));
            
            foreach (var file in files)
            {
                var task = HandleFile(file);
                task.Wait();
            }
            Console.WriteLine("No More Files to rename!");
            Console.ReadLine();
        }

        static DateTime GetDateTime(DateTime? dateTime)
        {
            DateTime date = new DateTime();
            if (dateTime.HasValue)
                date = dateTime.Value;
            return date;
        }

        static async Task HandleFile(FileInfo file)
        {
            var fileName = Path.GetFileNameWithoutExtension(file.FullName);
            Console.WriteLine("Processing File: '{0}'", file.FullName);
            var movies = await SearchForMovie(fileName);
            Console.WriteLine("Which Movie is this file? (enter -1 to skip)");
            var count = 1;
            foreach (var movie in movies.Results)
            {
                Console.WriteLine("  [{0}] {1} - {2} - https://www.themoviedb.org/movie/{3}",
                    count++,
                    GetDateTime(movie.ReleaseDate).Year,
                    movie.Title,
                    movie.Id
                );
            }
            var movieNumber = -1;
            var userInput = Console.ReadLine();
            int.TryParse(userInput, out movieNumber);
            var index = movieNumber - 1;
            if (index < 0 || index > movies.Results.Count() - 1)
                Console.WriteLine("Not renaming '{0}'", fileName);
            else
            {
                var searchMovie = movies.Results.ToArray()[index];
                var fullMovie = await GetMovie(searchMovie.Id);

                using (TagLib.File tagFile = TagLib.File.Create(file.FullName, "video/mp4", ReadStyle.Average))
                {
                    TagLib.Mpeg4.AppleTag customTag = (TagLib.Mpeg4.AppleTag)tagFile.GetTag(TagLib.TagTypes.Apple, true);

                    // name 
                    customTag.Title = fullMovie.Title;

                    // STIK || Media Type Tag
                    customTag.ClearData("stik");
                    var stikVector = new TagLib.ByteVector();
                    stikVector.Add((byte)9);
                    customTag.SetData("stik", stikVector, (int)TagLib.Mpeg4.AppleDataBox.FlagType.ContainsData);

                    // Short Description
                    customTag.ClearData("desc");
                    customTag.SetText("desc", ToShortDescription(fullMovie.Overview));

                    // Long Description
                    customTag.ClearData("ldes");
                    customTag.SetText("ldes", fullMovie.Overview);

                    // Release Date YYYY-MM-DD 
                    var releaseDate = GetDateTime(fullMovie.ReleaseDate);
                    customTag.Year = (uint)releaseDate.Year;
                    customTag.ClearData("tdrl");
                    customTag.SetText("tdrl", fullMovie.Overview);

                    // Genre 
                    var mainGenre = fullMovie.Genres.Select(g => g.Name).First();
                    customTag.Genres = new string[] { mainGenre };

                    // Cast / Actors
                    customTag.Performers = fullMovie.Credits.Cast.Select(c => c.Name).ToArray();

                    // HD Video
                    //customTag.ClearData("hdvd");
                    var inputFile = new MediaFile { Filename = file.FullName };
                    using (var engine = new Engine())
                    {
                        engine.GetMetadata(inputFile);
                    }
                    if (isHd(inputFile.Metadata.VideoData.FrameSize))
                    {
                        var hdvdVector = new TagLib.ByteVector();
                        hdvdVector.Add(Convert.ToByte(true));
                        customTag.SetData("hdvd", hdvdVector, (int)TagLib.Mpeg4.AppleDataBox.FlagType.ContainsData);
                    }

                    // Artwork / Poster
                    if (!String.IsNullOrWhiteSpace(fullMovie.Poster))
                    {
                        Console.WriteLine("    getting movie poster");
                        var artworkUrl = "http://image.tmdb.org/t/p/original" + fullMovie.Poster;

                        using (var client = new HttpClient())
                        {
                            var response = await client.GetAsync(artworkUrl);
                            var bytes = await response.Content.ReadAsByteArrayAsync();
                            tagFile.Tag.Pictures = new IPicture[] { new TagLib.Picture(bytes) };
                        }
                    }

                    tagFile.Save();
                }

                var newFileName = SanitizeFileName(fullMovie.Title) + Path.GetExtension(file.FullName);
                var folder = ConfigurationManager.AppSettings["to"];
                var newPath = Path.Combine(folder, newFileName);
                Console.WriteLine("    moving file to {0}", newPath);
                file.MoveTo(newPath);

            }
            Console.WriteLine("=========================================================");
        }

        static bool isHd(string frameSize)
        {
            var d = frameSize.Split('x');
            var height = int.Parse(d[1]);
            return height > 700;

        }

        static string SanitizeFileName(string filename)
        {
            string regex = String.Format(@"[{0}]+", Regex.Escape(new string(Path.GetInvalidFileNameChars())));
            return Regex.Replace(filename, regex, "");
        }

        static string ToShortDescription(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length < 251)
                return value;
            return value.Substring(0, 251) + "...";
        }

        static async Task<Movie> GetMovie(int id)
        {
            var client = new ServiceClient("dc272f1ee966ae5074280085a087c231");
            return await client.Movies.GetAsync(id, "en", true, System.Threading.CancellationToken.None);
        }

        static async Task<Movies> SearchForMovie(string fileName)
        {
            // Remove things like '-', '_', 'blu-ray' to be more search friendly
            fileName = fileName.ToLowerInvariant();
            fileName = Regex.Replace(fileName, "t[0-9][0-9]", "");
            fileName = fileName.Replace("blu-ray", "");
            fileName = fileName.Replace("_", " ");
            fileName = fileName.Replace("-", " ");

            Console.WriteLine("Searching for '{0}'", fileName);
            var client = new ServiceClient("dc272f1ee966ae5074280085a087c231");
            return await client.Movies.SearchAsync(fileName, "en", true, 1, System.Threading.CancellationToken.None);
        }
    }
}
