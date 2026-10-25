using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
try { return (await client.GetAsync("http://127.0.0.1:8080/health")).IsSuccessStatusCode ? 0 : 1; }
catch { return 1; }
