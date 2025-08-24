using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Newtonsoft.Json;
using WordPressPCL;
using WordPressPCL.Models;
using WordPressPCL.Utility;

namespace HoloGhost
{
    internal static class Program
    {
        private static async Task Main()
        {
            // increased pm.max_children to 50 in /etc/php/7.4/fpm/pool.d/www.conf and did service php7.4-fpm restart and pm.start_servers to 5

            var httpClient = new HttpClient();
            var html = new HtmlDocument();
            
            var oldWP = new WordPressClient("https://holographica.space/wp-json");

            var newWP = new WordPressClient(new Uri("http://95.163.222.135//wp-json"));
            newWP.Auth.UseBearerAuth(JWTPlugin.JWTAuthByEnriqueChavez);
            await newWP.Auth.RequestJWTokenAsync("unnanego", "GnomesAreReal69");
            var result = await newWP.Auth.IsValidJWTokenAsync();
            if (!result)
            {
                Console.WriteLine("Authentication failed");
                return;
            }

            var updatedPosts = new List<int>();

            List<Post> oldPosts = null;
            List<Post> newPosts = null;
            List<Tag> oldTags = null;
            List<Tag> newTags = null;
            List<User> oldUsers = null;
            List<User> newUsers = null;
            List<Category> oldCategories = null;
            List<Category> newCategories = null;

            // await LoadLocalData(true, true);

            await DownloadAndSaveAllData();

            async Task LoadLocalData(bool refreshNewPosts, bool loadOld)
            {
                if (loadOld)
                {
                    oldPosts = JsonConvert.DeserializeObject<List<Post>>(await File.ReadAllTextAsync("oldPosts.txt"));
                    oldTags = JsonConvert.DeserializeObject<List<Tag>>(await File.ReadAllTextAsync("oldTags.txt"));
                    oldUsers = JsonConvert.DeserializeObject<List<User>>(await File.ReadAllTextAsync("oldUsers.txt"));
                    oldCategories = JsonConvert.DeserializeObject<List<Category>>(await File.ReadAllTextAsync("oldCategories.txt"));
                }

                newPosts = refreshNewPosts ? newWP.Posts.GetAllAsync().Result.ToList() : JsonConvert.DeserializeObject<List<Post>>(await File.ReadAllTextAsync("newPosts.txt"));
                newTags = JsonConvert.DeserializeObject<List<Tag>>(await File.ReadAllTextAsync("newTags.txt"));
                newUsers = JsonConvert.DeserializeObject<List<User>>(await File.ReadAllTextAsync("newUsers.txt"));
                newCategories = JsonConvert.DeserializeObject<List<Category>>(await File.ReadAllTextAsync("newCategories.txt"));

                if (refreshNewPosts) await File.WriteAllTextAsync("newPosts.txt", JsonConvert.SerializeObject(newPosts));
            }

            async Task DownloadAndSaveAllData()
            {
                oldPosts = oldWP.Posts.GetAllAsync().Result.ToList();
                newPosts = newWP.Posts.GetAllAsync().Result.ToList();

                oldUsers = oldWP.Users.GetAllAsync().Result.ToList();
                newUsers = newWP.Users.GetAllAsync().Result.ToList();
                oldTags = oldWP.Tags.GetAllAsync().Result.ToList();
                newTags = newWP.Tags.GetAllAsync().Result.ToList();
                oldCategories = oldWP.Categories.GetAllAsync().Result.ToList();
                newCategories = newWP.Categories.GetAllAsync().Result.ToList();

                await File.WriteAllTextAsync("oldUsers.txt", JsonConvert.SerializeObject(oldUsers));
                await File.WriteAllTextAsync("newUsers.txt", JsonConvert.SerializeObject(newUsers));
                await File.WriteAllTextAsync("oldTags.txt", JsonConvert.SerializeObject(oldTags));
                await File.WriteAllTextAsync("newTags.txt", JsonConvert.SerializeObject(newTags));
                await File.WriteAllTextAsync("oldPosts.txt", JsonConvert.SerializeObject(oldPosts));
                await File.WriteAllTextAsync("newPosts.txt", JsonConvert.SerializeObject(newPosts));
                await File.WriteAllTextAsync("oldCategories.txt", JsonConvert.SerializeObject(oldCategories));
                await File.WriteAllTextAsync("newCategories.txt", JsonConvert.SerializeObject(newCategories));
            }

            var oldTagsDict = new Dictionary<int, string>();
            var oldUsersDict = new Dictionary<int, string>();
            var categoriesDict = new Dictionary<int, int>();
            var newTagsDict = new Dictionary<string, int>();
            var newUsersDict = new Dictionary<string, int>();

            if (oldTags != null)
            {
                foreach (var oldTag in oldTags)
                {
                    oldTagsDict[oldTag.Id] = oldTag.Slug;
                }
            }


            if (oldUsers != null)
            {
                foreach (var user in oldUsers)
                {
                    oldUsersDict[user.Id] = user.Slug;
                }
            }

            if (oldCategories != null)
            {
                foreach (var oldCategory in oldCategories)
                {
                    foreach (var newCategory in newCategories.Where(newCategory => oldCategory.Name == newCategory.Name))
                    {
                        categoriesDict[oldCategory.Id] = newCategory.Id;
                    }
                }
            }
            
            foreach (var newTag in newTags)
            {
                newTagsDict[newTag.Slug] = newTag.Id;
            }

            foreach (var user in newUsers)
            {
                newUsersDict[user.Slug] = user.Id;
            }

            await ProcessPosts();

            async Task ProcessPosts()
            {
                // going through old posts and checking if each of them is already moved, if not, then moves it
                // foreach (var oldPost in oldPosts.Where(post => post.Id is 1471 or 8075 or 5145)) 
                foreach (var oldPost in oldPosts)
                    // foreach (var oldPost in oldPosts.GetRange(0, 20)) //	this allows you to test on particular posts
                {
                    WriteProgress();
                    if (CheckIfExists(out var newPost))
                    {
                        Console.Write("Post already exists: " + LinkTrimmer(oldPost.Link) + "\n");

                        // await ChangeImagesLinks();

                        // fix wrong tags if you like me didn't do it the right way initially
                        async Task UpdateTags()
                        {
                            var oldPostTags = oldPost.Tags;
                            var newPostTags = newPost.Tags;
                            var oldPostSlugs = oldPostTags.Select(id => oldTags.First(tag => tag.Id == id).Slug).ToList();
                            var newPostSlugs = newPostTags.Select(id => newTags.First(tag => tag.Id == id).Slug).ToList();
                            var slugsEqual = true;
                            if (oldPostSlugs.Count == newPostSlugs.Count)
                            {
                                for (var i = 0; i < oldPostSlugs.Count; i++)
                                {
                                    if (oldPostSlugs[i] == newPostSlugs[i]) continue;

                                    // Console.WriteLine("Slugs are different: " + oldPostSlugs[i] + " : " + newPostSlugs[i]);
                                    slugsEqual = false;
                                }
                            }

                            if (slugsEqual) return;

                            newPost.Tags = new List<int>(oldPost.Tags.Count);

                            for (var i = 0; i < oldPostTags.Count; i++)
                            {
                                var oldTag = oldPostTags[i];
                                var oldTagSlug = oldTagsDict[oldTag];
                                var oldTagName = oldTags.First(tag => tag.Id == oldTag).Name;
                                if (!newTagsDict.ContainsKey(oldTagSlug))
                                {
                                    Tag tag = null;
                                    while (tag == null)
                                    {
                                        try
                                        {
                                            tag = await newWP.Tags.CreateAsync(new Tag {Slug = oldTagSlug, Name = oldTagName});
                                            newTagsDict[tag.Slug] = tag.Id;
                                        }
                                        catch
                                        {
                                            Console.WriteLine($"Failed to create tag {oldTagSlug}, waiting...");
                                            await Task.Delay(12 * 1000);
                                        }
                                    }
                                }

                                var newTag = newTagsDict[oldTagSlug];
                                newPost.Tags[i] = newTag;
                            }
                        }

                        async Task SyncMeta()
                        {
                            if (!File.Exists("updatedPosts.txt"))
                            {
                                await File.WriteAllTextAsync("updatedPosts.txt", JsonConvert.SerializeObject(updatedPosts));
                            }

                            var json = await File.ReadAllTextAsync("updatedPosts.txt");
                            updatedPosts = JsonConvert.DeserializeObject<List<int>>(json);
                            if (updatedPosts.Contains(newPost.Id))
                            {
                                Console.WriteLine("Post with id " + newPost.Id + " meta already updated.");
                                return;
                            }

                            if (newPost.Categories.Contains(7805) || newPost.Categories.Contains(7801))
                            {
                                Console.WriteLine("No thumbnail category.");
                                return;
                            }

                            if (GetMedia(out var media)) return;

                            if (int.TryParse(newPost.Meta._thumbnail_id.ToString(), out int id) && media.Last().Id == id)
                            {
                                Console.WriteLine("Post thumbnail id is the same as last media id.");
                                updatedPosts.Add(newPost.Id);
                                await File.WriteAllTextAsync("updatedPosts.txt", JsonConvert.SerializeObject(updatedPosts));
                                return;
                            }

                            var oldPostMeta = oldPost.Meta;
                            newPost.Meta = oldPostMeta;
                            newPost.Meta._thumbnail_id = media.Last().Id;
                            await newWP.Posts.UpdateAsync(newPost);
                            updatedPosts.Add(newPost.Id);
                            await File.WriteAllTextAsync("updatedPosts.txt", JsonConvert.SerializeObject(updatedPosts));
                            Console.WriteLine("Updated meta");
                        }

                        //todo process videos

                        // async Task ChangeImagesLinks()
                        // {
                        //     var content = newPost.Content.Rendered;
                        //     html.LoadHtml(content);
                        //     var nodes = html.DocumentNode.SelectNodes("//img");
                        //     if (nodes == null) return;
                        //     var replaced = false;
                        //     foreach (var node in nodes)
                        //     {
                        //         var attributes = node.Attributes;
                        //         for (var i = attributes.Count - 1; i >= 0; i--)
                        //         {
                        //             var attribute = attributes[i];
                        //             switch (attribute.Name)
                        //             {
                        //                 case "src":
                        //                 {
                        //                     var link = attribute.Value;
                        //                     if (!string.IsNullOrEmpty(link) && !IsImageAvailable(link)) Console.WriteLine("Post " + newPost.Slug + " is missing " + link);
                        //                     break;
                        //                 }
                        //                 case "srcset":
                        //                     content = content.Replace(attribute.Value, "").Replace("srcset=\"\"", "");
                        //                     replaced = true;
                        //                     break;
                        //             }
                        //
                        //             // if (attribute.Value.Contains("//test.hol")) replaced = true; else continue;
                        //             // var newValue = attribute.Value.Replace("https://test.holographica.space/wp-content", "https://holographica.space/wp-content");
                        //         }
                        //     }
                        //
                        //     var lastLink = content.Split("<strong>Далее:").Last();
                        //     if (!lastLink.Contains("target=\"_blank\""))
                        //     {
                        //         Console.WriteLine("Skipping.\n");
                        //         return;
                        //     }
                        //
                        //     var newLastLink = lastLink.Replace("target=\"_blank\" rel=\"noopener\"", "");
                        //     content = content.Replace(lastLink, newLastLink);
                        //
                        //     newPost.Content = new Content(content);
                        //     newPost.Meta = CheckMetaForNonStrings(newPost.Meta);
                        //     // if (replaced) await newWP.Posts.Update(newPost);
                        //     await newWP.Posts.UpdateAsync(newPost);
                        //
                        //     bool IsImageAvailable(string imageUrl)
                        //     {
                        //         try
                        //         {
                        //             var request = (HttpWebRequest) WebRequest.Create(imageUrl.Split(" ").First());
                        //             using var response = (HttpWebResponse) request.GetResponse();
                        //             return response.StatusCode == HttpStatusCode.OK;
                        //         }
                        //         catch
                        //         {
                        //             return false;
                        //         }
                        //     }
                        // }

                        dynamic CheckMetaForNonStrings(dynamic meta)
                        {
                            meta.tdm_status = "";
                            meta.tdm_grid_status = "";
                            return meta;
                        }
                    }
                    else await MovePost(oldPost);

                    bool CheckIfExists(out Post nP)
                    {
                        var exists = newPosts.Any(p => p.Slug == $"{oldPost.Slug}-{oldPost.Id}");
                        nP = exists ? newPosts.Find(p => p.Slug == oldPost.Slug + "-" + oldPost.Id) : null;

                        return exists;
                    }

                    void WriteProgress()
                    {
                        Console.Write("Progress " + Truncate(oldPosts.IndexOf(oldPost) / (decimal) oldPosts.Count * 100, 2) + "% ");
                        // Console.WriteLine();

                        decimal Truncate(decimal d, byte decimals)
                        {
                            var r = Math.Round(d, decimals);

                            return d switch
                            {
                                > 0 when r > d => r - new decimal(1, 0, 0, false, decimals),
                                < 0 when r < d => r + new decimal(1, 0, 0, false, decimals),
                                _ => r
                            };
                        }
                    }

                    bool GetMedia(out List<MediaItem> media)
                    {
                        var query = new MediaQueryBuilder {Parents = new List<int>(newPost.Id)};
                        media = newWP.Media.QueryAsync(query).Result.ToList();
                        if (media.Count == 0)
                        {
                            Console.WriteLine("No media in post " + newPost.Slug + " found.");
                            return false;
                        }

                        return true;
                    }
                }
            }

            async Task MovePost(Post postOut)
            {
                Console.WriteLine("Creating post with slug " + postOut.Slug);
                var oldPostMeta = postOut.Meta;
                var tags = new List<int>(postOut.Tags.Count);
                for (var i = 0; i < postOut.Tags.Count; i++)
                {
                    var tagId = postOut.Tags[i];
                    var slug = oldTagsDict[tagId];
                    int newId;
                    if (newTagsDict.ContainsKey(slug)) newId = newTagsDict[slug];
                    else
                    {
                        var tag = await newWP.Tags.CreateAsync(new Tag {Slug = slug, Name = oldTags?.First(t => t.Id == tagId).Name});
                        newTags.Add(tag);
                        newTagsDict[slug] = tag.Id;
                        newId = tag.Id;
                    }

                    tags[i] = newId;
                }

                var newPost = new Post
                {
                    Title = new Title(postOut.Title.Rendered),
                    Content = new Content(postOut.Content.Rendered),
                    Excerpt = new Excerpt(postOut.Excerpt.Rendered),
                    Meta = oldPostMeta,
                    Status = postOut.Status,
                    Tags = tags,
                    Links = postOut.Links,
                    Date = postOut.Date,
                    Slug = postOut.Slug + "-" + postOut.Id
                };

                newPost = await newWP.Posts.CreateAsync(newPost);

                var content = postOut.Content.Rendered;
                html.LoadHtml(content);
                var nodes = html.DocumentNode.SelectNodes("//img");
                var featuredImageFound = false;
                var featuredImage = -1;
                if (nodes != null)
                {
                    foreach (var node in nodes)
                    {
                        var attributes = node.Attributes;
                        foreach (var attribute in attributes)
                        {
                            if (attribute.Name != "src") continue;

                            var fileName = Regex.Replace(LinkTrimmer(attribute.Value), @"[^\u0000-\u007F]+", string.Empty);
                            if (fileName.Length > 100) fileName = fileName[..100] + "." + fileName.Split('.').Last();

                            if (fileName.Split('.').Last().Contains("gif"))
                            {
                                var responseStream = await httpClient.GetStreamAsync(attribute.Value);
                                var image = await Image.LoadAsync(responseStream);
                                await image.SaveAsGifAsync(fileName);
                            }
                            else
                            {
                                try
                                {
                                    var responseBody = await httpClient.GetByteArrayAsync(attribute.Value);
                                    await File.WriteAllBytesAsync(fileName, responseBody);
                                }
                                catch
                                {
                                    Console.WriteLine("404, moving to the next image.");
                                    continue;
                                }
                            }

                            var stream = new FileStream(fileName, FileMode.Open);
                            var mediaItem = await newWP.Media.CreateAsync(stream, fileName);
                            mediaItem.Post = newPost.Id;
                            await newWP.Media.UpdateAsync(mediaItem);

                            if (!featuredImageFound)
                            {
                                featuredImageFound = true;
                                featuredImage = mediaItem.Id;
                            }

                            File.Delete(fileName);

                            content = content.Replace(attribute.Value, mediaItem.SourceUrl);
                        }
                    }
                }
                    

                var convertedCategories = new List<int>(postOut.Categories.Count);
                for (var i = 0; i < postOut.Categories.Count; i++) convertedCategories[i] = categoriesDict[postOut.Categories[i]];

                newPost.Author = newUsersDict[oldUsersDict[postOut.Author]];
                newPost.Categories = convertedCategories;

                // newPost.FeaturedMedia = featuredImage; // since we're changing the meta, this doesn't work, thus next line?
                newPost.Meta._thumbnail_id = featuredImage;
                newPost.Content = new Content(content);

                await newWP.Posts.UpdateAsync(newPost);
            }
        }

        private static string LinkTrimmer(string link)
        {
            // get slug from post's url
            return link.Split('/').Last();
        }

        // check for posts with duplicate titles and print them to console
        private static void FindDuplicateTitles(IEnumerable<Post> posts)
        {
            var query = posts.GroupBy(x => x.Title.Rendered)
                .Where(g => g.Count() > 1)
                .Select(y => y.Key)
                .ToList();
            foreach (var title in query)
            {
                Console.WriteLine(title);
            }
        }
    }
}