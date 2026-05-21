#r "nuget: Microsoft.Data.Sqlite, 9.0.0"
using Microsoft.Data.Sqlite;
var conn = new SqliteConnection("Data Source=C:\\Repos\\My\\SiteDevelopmentTool\\src\\ImagineWeb.Api\\bin\\Debug\\net10.0\\hunter.db");
conn.Open();
var cmd = conn.CreateCommand();

// 1. Overall status breakdown
cmd.CommandText = "SELECT Status, COUNT(*) FROM Pages GROUP BY Status ORDER BY Status";
var r = cmd.ExecuteReader();
Console.WriteLine("=== STATUS BREAKDOWN ===");
var statusNames = new[]{"Discovered","Scraping","Scraped","Queued","Analyzing","Analyzed","Failed","Skipped","Dismissed"};
while(r.Read()) {
    var st = (int)r.GetInt64(0);
    var name = st < statusNames.Length ? statusNames[st] : $"Unknown({st})";
    Console.WriteLine($"  {st} ({name}): {r.GetInt64(1)}");
}
r.Close();

// 2. For analyzed pages: score distribution
cmd.CommandText = "SELECT InterestingnessScore, COUNT(*) FROM Pages WHERE Status = 5 GROUP BY InterestingnessScore ORDER BY InterestingnessScore DESC";
r = cmd.ExecuteReader();
Console.WriteLine("\n=== ANALYZED (Status=5) SCORE DISTRIBUTION ===");
while(r.Read()) Console.WriteLine($"  Interest={r.GetInt64(0)}: {r.GetInt64(1)} pages");
r.Close();

// 3. Phase2 analysis: the score-8 page details
cmd.CommandText = "SELECT Id, Title, Url, InterestingnessScore, ProfitScore, Phase2Skipped, FeasibilityScore, ShouldDeepDive, AnalyzedAt FROM Pages WHERE InterestingnessScore >= 8 OR ProfitScore >= 8";
r = cmd.ExecuteReader();
Console.WriteLine("\n=== PAGES WITH SCORE >= 8 (Phase2 should trigger) ===");
while(r.Read()) {
    Console.WriteLine($"  ID: {r.GetInt64(0)} | I:{r.GetInt64(3)} P:{r.GetInt64(4)} | Phase2Skipped: {(r.IsDBNull(5)?"null":r.GetInt64(5).ToString())} | Feasibility: {(r.IsDBNull(6)?"0":r.GetInt64(6).ToString())} | DeepDive: {(r.IsDBNull(7)?"null":r.GetInt64(7).ToString())}");
    Console.WriteLine($"    Title: {(r.IsDBNull(1)?"?":r.GetString(1))}");
    Console.WriteLine($"    URL: {(r.IsDBNull(2)?"?":r.GetString(2))}");
    Console.WriteLine($"    AnalyzedAt: {(r.IsDBNull(8)?"NULL":r.GetString(8))}");
}
r.Close();

// 4. Check recent timestamps to see if scan is still running
cmd.CommandText = "SELECT MAX(AnalyzedAt) as LastAnalyzed, MAX(DiscoveredAt) as LastDiscovered FROM Pages";
r = cmd.ExecuteReader();
Console.WriteLine("\n=== SCAN TIMING ===");
if(r.Read()) {
    Console.WriteLine($"  Last analyzed: {(r.IsDBNull(0)?"NULL":r.GetString(0))}");
    Console.WriteLine($"  Last discovered: {(r.IsDBNull(1)?"NULL":r.GetString(1))}");
}
r.Close();

// 5. Check if there are pages with score>=7 and profitScore>=7 (either axis triggers Phase2)
cmd.CommandText = @"SELECT Id, Title, InterestingnessScore, ProfitScore, Phase2Skipped, FeasibilityScore 
FROM Pages WHERE Status=5 AND (InterestingnessScore > 7 OR ProfitScore > 7) ORDER BY InterestingnessScore DESC";
r = cmd.ExecuteReader();
Console.WriteLine("\n=== PAGES WHERE bestScore > 7 (Phase2 should have triggered) ===");
while(r.Read()) {
    Console.WriteLine($"  ID:{r.GetInt64(0)} I:{r.GetInt64(2)} P:{r.GetInt64(3)} | Phase2Skipped:{(r.IsDBNull(4)?"null":r.GetInt64(4).ToString())} Feasibility:{(r.IsDBNull(5)?"0":r.GetInt64(5).ToString())} | {(r.IsDBNull(1)?"?":r.GetString(1))}");
}
r.Close();

// 6. Sample of Failed pages - check what error pattern
cmd.CommandText = "SELECT Id, Title, Url, AiSummary, AiRecommendation FROM Pages WHERE Status = 6 LIMIT 5";
r = cmd.ExecuteReader();
Console.WriteLine("\n=== FAILED PAGE SAMPLES (Status=6) ===");
while(r.Read()) {
    Console.WriteLine($"  ID:{r.GetInt64(0)} | {(r.IsDBNull(1)?"NULL":r.GetString(1))}");
    Console.WriteLine($"    URL: {(r.IsDBNull(2)?"NULL":r.GetString(2))}");
    Console.WriteLine($"    Summary: {(r.IsDBNull(3)?"NULL":r.GetString(3))}");
    Console.WriteLine($"    Recommendation: {(r.IsDBNull(4)?"NULL":r.GetString(4))}");
}
r.Close();

// 7. Pages with non-zero FeasibilityScore (successful Phase2)
cmd.CommandText = "SELECT COUNT(*) FROM Pages WHERE FeasibilityScore > 0";
r = cmd.ExecuteReader();
r.Read();
Console.WriteLine($"\n=== Pages with successful Phase2 (FeasibilityScore > 0): {r.GetInt64(0)} ===");
r.Close();
