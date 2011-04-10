using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.IO;
using HtmlAgilityPack;
using System.Text.RegularExpressions;

namespace CrawlYahooBoxscores
{
    class GameRow
    {
        public string gid;
        public string date;
        public string awayTeam;
        public string homeTeam;
    }

    class PlayerRow
    {
        public string gid;
        public string teamCode;
        public string playerName;
        public int min;
        public int fga;
        public int fgm;
        public int tpa;
        public int tpm;
        public int fta;
        public int ftm;
        public int oreb;
        public int dreb;
        public int ast;
        public int to;
        public int stl;
        public int blk;
        public int pf;
    }

    class Program
    {
        static void Main(string[] args)
        {
            string[] lines = File.ReadAllLines(TEAM_CODE_FILE);

            foreach (string line in lines)
            {
                string[] parts = line.Split('\t');
                string code = parts[0];
                string name = parts[1];
                TeamMapping[name] = code;
                TeamMapping[code] = name;
            }

            string mode = args[0];

            // set paths
            if (args.Length > 3)
            {
                CACHE_PATH = args[3];
                OUTPUT_PATH = args[4];
            }

            if (mode == "crawl")
            {
                int startYear = int.Parse(args[1]);
                int endYear = int.Parse(args[2]);// set paths

                ErrorLog = new StreamWriter(Path.Combine(OUTPUT_PATH, "Error.log"));

                /*
                for (int year = endYear; year >= startYear; year--)
                {
                    foreach (string team in teams)
                    {
                        CrawlTeam(team, year);
                    }
                }
                */

                // seed the crawl with the first month of the seasons
                Dictionary<string, bool> dates = new Dictionary<string, bool>();
                HashSet<string> visited = new HashSet<string>();
                string[] months = new string[] { "11", "12", "01", "02", "03", "04" };

                for (int year = endYear; year >= startYear; year--)
                {
                    foreach (string month in months)
                    {
                        string yearStr = (month.StartsWith("0") ? (year + 1).ToString() : year.ToString());
                        string start = yearStr + "-" + month + "-01";

                        CrawlDate(start, visited);
                    }
                }

                // close open streams if we have them
                if (GameStream != null) { GameStream.Close(); }
                if (PlayerStream != null) { PlayerStream.Close(); }
                ErrorLog.Close();
            }

            if (mode == "game")
            {
                CrawlGame(args[1]);
            }
        }

        const string TEAM_CODE_FILE = @"YahooTeamCodes.txt";
        static Dictionary<string, string> TeamMapping = new Dictionary<string, string>();
        static string CACHE_PATH = @"C:\Users\Lee\Sources\NCAA_2011_Tournament\Cache";
        static string OUTPUT_PATH = @"C:\Users\Lee\Sources\NCAA_2011_Tournament\Output";
        static Regex GidRegex = new Regex(@"/ncaab/recap\?gid=(\d+)", RegexOptions.Compiled);
        static Regex DateRegex = new Regex(@"/ncaab/scoreboard\?d=(\d\d\d\d-\d\d-\d\d)&c=all""\s+class=yspinfo", RegexOptions.Compiled);

        static StreamWriter GameStream;
        static StreamWriter PlayerStream;
        static StreamWriter ErrorLog;

        private static void CrawlDate(string date, HashSet<string> visited)
        {
            if (visited.Contains(date)) { return; }
            visited.Add(date);

            // crawl
            Uri dateUrl = new Uri(@"http://rivals.yahoo.com/ncaa/basketball/scoreboard?d=" + date + @"&c=all");
            string html = DownloadAndCache(dateUrl);

            // grab all boxscore links and follow them
            MatchCollection scoreMatches = GidRegex.Matches(html);
            HashSet<string> gids = new HashSet<string>();
            foreach (Match match in scoreMatches)
            {
                if (match.Success)
                {
                    gids.Add(match.Groups[1].Value);
                }
            }

            foreach (string gid in gids)
            {
                CrawlGame(gid);
            }

            // grab all date links and recursively crawl them
            MatchCollection dateMatches = DateRegex.Matches(html);

            foreach (Match match in dateMatches)
            {
                if (match.Success)
                {
                    CrawlDate(match.Groups[1].Value, visited);
                }
            }
        }

        private static void CrawlTeam(string teamCode, int year)
        {
            Uri scheduleUrl = new Uri(@"http://rivals.yahoo.com/ncaa/basketball/teams/" + teamCode + @"/schedule?y=" + year.ToString());
            string html = DownloadAndCache(scheduleUrl);

            HtmlDocument document = new HtmlDocument();
            document.LoadHtml(html);

            // manipulate the document
            // this is the path to the table containing the schedule
            HtmlNode scheduleNode = document.DocumentNode.SelectSingleNode(@"/html[1]/body[1]/div[1]/table[1]/tr[1]/td[1]/table[4]");

            // only parse home games and never away games (Rivals.com does not have the notion of neutral courts luckily)
            if (scheduleNode == null)
            {
                ErrorLog.WriteLine("Unable to crawl team " + teamCode + " " + year.ToString());
                ErrorLog.Flush();
                return;
            }
            HtmlNodeCollection scheduleRows = scheduleNode.SelectNodes(@"tr");

            foreach (HtmlNode row in scheduleRows)
            {
                HtmlNodeCollection cells = row.SelectNodes(@"td");
                // check for a link to a boxscore (which validates this as a row we care about)
                if (cells.Count == 7)
                {
                    string detailCellHtml = cells[3].InnerHtml;
                    Match m = GidRegex.Match(detailCellHtml);
                    if (m.Success) // valid
                    {
                        // check for away game or home game
                        if (!cells[2].InnerText.StartsWith("at"))
                        {
                            // get the game id
                            string gid = m.Groups[1].Value;
                            CrawlGame(gid);
                        }
                    }
                }
            }
        }

        private static void CrawlGame(string gid)
        {
            string gameId = gid.ToString();
            string url = @"http://rivals.yahoo.com/ncaa/basketball/boxscore?gid=" + gameId;
            if (long.Parse(gameId) > 201103100000)
            {
                url += "&old_bs=1";
            }
            Uri boxscoreUrl = new Uri(url);
            string html = DownloadAndCache(boxscoreUrl);
            HtmlDocument document = new HtmlDocument();
            document.LoadHtml(html);

            // the date is the first 8 integers of the game id
            string date = gid.Substring(0, 8);

            // get the team code values from the summarized table
            // /html[1]/head[1]/body[1]/div[1]/table[1]/tr[1]/td[1]/table[2]/tr[1]/td[1]/div[1]/table[3]

            // path to the top team code URL
            HtmlNode awayUrlNode =
                document.DocumentNode.SelectSingleNode(@"/html[1]/head[1]/body[1]/div[1]/table[1]/tr[1]/td[1]/table[2]/tr[1]/td[1]/div[1]/table[3]/tr[1]/td[2]/table[1]/tr[1]/td[1]/table[1]/tr[1]/td[1]/table[1]/tr[4]/td[2]");
            HtmlNode homeUrlNode =
                document.DocumentNode.SelectSingleNode(@"/html[1]/head[1]/body[1]/div[1]/table[1]/tr[1]/td[1]/table[2]/tr[1]/td[1]/div[1]/table[3]/tr[1]/td[2]/table[1]/tr[1]/td[1]/table[1]/tr[1]/td[1]/table[1]/tr[6]/td[2]");

            // get the teamcode from the node
            string awayTeamCode = GetTeamCodeFromNode(awayUrlNode);
            string homeTeamCode = GetTeamCodeFromNode(homeUrlNode);

            HtmlNode awayTable =
                document.DocumentNode.SelectSingleNode(@"/html[1]/head[1]/body[1]/div[1]/table[1]/tr[1]/td[1]/table[2]/tr[1]/td[1]/div[1]/table[5]");
            HtmlNode homeTable =
                document.DocumentNode.SelectSingleNode(@"/html[1]/head[1]/body[1]/div[1]/table[1]/tr[1]/td[1]/table[2]/tr[1]/td[1]/div[1]/table[7]");

            // we have both tables and both teams set
            if (!string.IsNullOrEmpty(awayTeamCode) && !string.IsNullOrEmpty(homeTeamCode) && awayTable != null && homeTable != null)
            {
                // create the game object and write to disk
                GameRow row = new GameRow();
                row.date = date;
                row.gid = gid;
                row.awayTeam = awayTeamCode;
                row.homeTeam = homeTeamCode;
                WriteGameRow(row);

                // parse the top (away) table
                ParseScoreTable(awayTable, gid, awayTeamCode);

                // parse the bottom (home) table
                ParseScoreTable(homeTable, gid, homeTeamCode);
            }
            else
            {
                // could not parse this game, log it
                ErrorLog.WriteLine("Could not parse " + gid);
                ErrorLog.Flush();
            }
        }

        private static void ParseScoreTable(HtmlNode awayTable, string gid, string team)
        {
            HtmlNodeCollection rows = awayTable.SelectNodes("tr");
            // skip the first two rows which are the team name and the headers
            // also skip the last three rows which are spacers plus team info
            if (rows.Count > 5)
            {
                for (int i = 2; i < rows.Count - 3; i++)
                {
                    HtmlNode row = rows[i];
                    HtmlNodeCollection cells = row.SelectNodes("td");
                    // we should have 13 cells
                    if (cells.Count == 13)
                    {
                        PlayerRow player = new PlayerRow();
                        // get the name
                        HtmlNode nameNode = cells[0].SelectSingleNode("a");
                        string name = (nameNode == null ? cells[0].InnerText : nameNode.InnerText);
                        // now set the stats
                        player.playerName = name;
                        player.gid = gid;
                        player.teamCode = team;
                        player.min = ParseSingleCell(cells[1]);
                        ParseFraction(cells[2].InnerText, out player.fga, out player.fgm);
                        ParseFraction(cells[3].InnerText, out player.tpa, out player.tpm);
                        ParseFraction(cells[4].InnerText, out player.fta, out player.ftm);
                        player.oreb = ParseSingleCell(cells[5]);
                        player.dreb = ParseSingleCell(cells[6]);
                        player.ast = ParseSingleCell(cells[7]);
                        player.to = ParseSingleCell(cells[8]);
                        player.stl = ParseSingleCell(cells[9]);
                        player.blk = ParseSingleCell(cells[10]);
                        player.pf = ParseSingleCell(cells[11]);
                        // skip cells[12] which is the total points

                        WritePlayerRow(player);
                    }
                }
            }

        }

        private static int ParseSingleCell(HtmlNode cell)
        {
            int value = 0;
            if (cell != null)
            {
                int.TryParse(cell.InnerText, out value);
            }

            return value;
        }

        private static void ParseFraction(string input, out int attempts, out int made)
        {
            attempts = 0;
            made = 0;

            string[] parts = input.Split('-');
            if (parts.Length == 2)
            {
                int.TryParse(parts[0], out made);
                int.TryParse(parts[1], out attempts);
            }
        }

        private static void WritePlayerRow(PlayerRow row)
        {
            if (PlayerStream == null)
            {
                PlayerStream = new StreamWriter(Path.Combine(OUTPUT_PATH, "Players.tsv"));
            }

            PlayerStream.WriteLine("{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\t{8}\t{9}\t{10}\t{11}\t{12}\t{13}\t{14}\t{15}\t{16}",
                row.playerName, row.gid, row.teamCode,
                row.min, row.fgm, row.fga,
                row.tpm, row.tpa, row.ftm, row.fta,
                row.oreb, row.dreb, row.ast, row.to, row.stl, row.blk, row.pf);
        }

        private static void WriteGameRow(GameRow row)
        {
            if (GameStream == null)
            {
                GameStream = new StreamWriter(Path.Combine(OUTPUT_PATH, "Games.tsv"));
            }

            GameStream.WriteLine("{0}\t{1}\t{2}\t{3}", row.gid, row.date, row.homeTeam, row.awayTeam);
        }

        private static string GetTeamCodeFromNode(HtmlNode node)
        {
            if (node == null) { return ""; }
            // see if there is a link element
            HtmlNode linkNode = node.SelectSingleNode("a");
            if (linkNode != null)
            {
                string relativeUrl = linkNode.Attributes[0].Value;
                // return the last part
                int index = relativeUrl.LastIndexOf('/');
                if (index > 0)
                {
                    return relativeUrl.Substring(index + 1);
                }
            }
            else
            {
                // there is no code, perform reverse lookup
                // look for <b> tag for the team
                HtmlNode boldNode = node.SelectSingleNode("b");
                if (boldNode != null)
                {
                    string name = boldNode.InnerText.Trim();

                    // strip out any seeding information
                    int index = Regex.Match(name, @"[A-Z]").Index;
                    if (index > 0)
                    {
                        name = name.Substring(index);
                    }

                    if (TeamMapping.ContainsKey(name)) { return TeamMapping[name]; }
                    else if (!string.IsNullOrEmpty(name) && name.Length > 0) ;
                    {
                        // it's actually a game, but it's against an unknown school
                        return "UNK";
                    }
                }
            }

            return "";
        }

        private static string GetLocationPathFromUri(Uri uri)
        {
            string path = "";
            int length = uri.Segments.Length;

            if (length >= 3)
            {
                string transformedQuery = "";

                if (!string.IsNullOrEmpty(uri.Query))
                {
                    transformedQuery = uri.Query.Substring(1).Replace("=", "_"); ;
                }

                string directory = uri.Segments[length - 1];
                string filename = uri.Segments[length - 2].Replace("/", "") + "_" + transformedQuery + ".html";

                path = Path.Combine(CACHE_PATH, Path.Combine(directory, filename));
            }

            return path;
        }

        static DateTime LastCrawlTime = DateTime.MinValue;
        private static string DownloadAndCache(Uri location)
        {
            // if we have it on disk, just read it
            string filePath = GetLocationPathFromUri(location);
            string document = "";

            if (File.Exists(filePath))
            {
                document = File.ReadAllText(filePath);
            }
            else
            {
                // wait three seconds between tries
                if (DateTime.Now.Subtract(LastCrawlTime).Seconds < 5)
                {
                    System.Threading.Thread.Sleep(5000);
                }

                bool networkSuccess = false;
                int backoff = 1;

                while (!networkSuccess)
                {
                    try
                    {
                        LastCrawlTime = DateTime.Now;
                        WebRequest request = WebRequest.Create(location);

                        WebResponse response = request.GetResponse();
                        StreamReader reader = new StreamReader(response.GetResponseStream());

                        document = reader.ReadToEnd();

                        response.Close();
                        networkSuccess = true;

                        // also cache it to disk
                        StreamWriter writer = new StreamWriter(filePath);
                        writer.Write(document);
                        writer.Close();
                    }
                    catch (Exception e)
                    {
                        ErrorLog.WriteLine("Network Exception: " + e.Message);
                        ErrorLog.Flush();

                        if (e.Message.Contains("999"))
                        {
                            backoff *= 2;
                            System.Threading.Thread.Sleep(backoff * 1000);
                        }
                    }
                }
            }

            return document;
        }
    }
}
