using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json;

namespace BingoSignalRClient
{
    class Program
    {
        private const string BASE_URL = "https://bingo-backend.zetabox.tn";
        private static int CARD_DELAY = int.Parse(Environment.GetEnvironmentVariable("CARD_DELAY")); // 1 hour
        private static int SELECT_DELAY = int.Parse(Environment.GetEnvironmentVariable("SELECT_DELAY")); // 1 hour
        private static int MAX_TIMER = int.Parse(Environment.GetEnvironmentVariable("MAX_TIMER"));

        private static int fail = 0;
        private static int notif = 0;
        private static readonly object lockObject = new object();

        // Track card IDs to detect duplicates across users
        private static readonly Dictionary<int, int> cardIdToUserMap = new Dictionary<int, int>();
        private static readonly object cardMapLock = new object();

        // Track selected numbers for each user to prevent duplicate selections
        private static readonly Dictionary<int, HashSet<int>> userSelectedNumbers = new Dictionary<int, HashSet<int>>();
        private static readonly Dictionary<int, HashSet<int>> pendingNumberSelections = new Dictionary<int, HashSet<int>>();
        private static readonly object selectedNumbersLock = new object();

        // List to store user tokens loaded from file
        private static List<UserToken> userTokens = new List<UserToken>();

        // Path to the tokens file (default value, can be overridden by command-line argument)
        private static string tokensFilePath = "tokens.json";

        static async Task Main(string[] args)
        {
            Console.WriteLine("Starting Bingo SignalR Client Simulation");

            // Check if a tokens file was specified as a command-line argument
            if (args.Length > 0)
            {
                tokensFilePath = args[0];
                Console.WriteLine($"Using tokens file: {tokensFilePath}");
            }
            else
            {
                Console.WriteLine($"No tokens file specified, using default: {tokensFilePath}");
            }

            // Load tokens from file
            LoadTokensFromFile();

            int userCount = userTokens.Count;
            Console.WriteLine($"Loaded {userCount} user tokens from file");

            if (userCount == 0)
            {
                Console.WriteLine("No tokens found in the file. Please add tokens to tokens.json");
                return;
            }

            // Create a list to hold all user simulation tasks
            var tasks = new List<Task>();

            for (int i = 0; i < userCount; i++)
            {
                int userIndex = i;

                // Run each user simulation without semaphore limiting
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await SimulateUser(userIndex);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error in user {userIndex} simulation: {ex.Message}");
                    }
                }));

                // Add slight delay between user spawns
                await Task.Delay(int.Parse(Environment.GetEnvironmentVariable("delay")));

                lock (lockObject)
                {
                    Console.WriteLine($"Fail: {fail}");
                    Console.WriteLine($"Notifications: {notif}");
                }
            }

            // Wait for all tasks to complete
            await Task.WhenAll(tasks);
            Console.WriteLine("Simulation completed");
        }

        // Load tokens from the JSON file
        private static void LoadTokensFromFile()
        {
            try
            {
                string filePath = Path.Combine(Directory.GetCurrentDirectory(), tokensFilePath);
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);

                    // Parse tokens as a simple array of strings
                    var tokenStrings = JsonConvert.DeserializeObject<List<string>>(json);

                    if (tokenStrings != null && tokenStrings.Count > 0)
                    {
                        Console.WriteLine($"Found {tokenStrings.Count} tokens in file");

                        foreach (var tokenString in tokenStrings)
                        {
                            try
                            {
                                // Parse the JWT token to extract the user ID
                                var handler = new JwtSecurityTokenHandler();
                                var token = handler.ReadJwtToken(tokenString);

                                // Try to get the user ID from the token claims
                                var userIdClaim = token.Claims.FirstOrDefault(c => c.Type == "IdUser");

                                if (userIdClaim != null && int.TryParse(userIdClaim.Value, out int userId))
                                {
                                    userTokens.Add(new UserToken
                                    {
                                        UserId = userId,
                                        AccessToken = tokenString
                                    });
                                }
                                else
                                {
                                    Console.WriteLine($"Warning: Could not extract user ID from token: {tokenString.Substring(0, 20)}...");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error parsing token: {ex.Message}");
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("No tokens found in the file or invalid format");
                    }
                }
                else
                {
                    Console.WriteLine($"Tokens file not found at {filePath}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading tokens from file: {ex.Message}");
            }
        }

        private static async Task SimulateUser(int userIndex)
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            try
            {
                // Get the user token from the loaded list
                if (userIndex >= userTokens.Count)
                {
                    throw new Exception($"User index {userIndex} exceeds available tokens count {userTokens.Count}");
                }

                var userToken = userTokens[userIndex];
                int userId = userToken.UserId;
                string token = userToken.AccessToken;

                Console.WriteLine($"User {userIndex}: Starting with userId {userId} and token...");

                // Update headers with token
                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                // Step 3: Connect to SignalR
                var connection = new HubConnectionBuilder()
                    .WithUrl($"{BASE_URL}/api/notificationsHub", options =>
                    {
                        options.AccessTokenProvider = () => Task.FromResult(token);
                    })
                    .WithAutomaticReconnect()
                    .Build();

                bool cardSelected = false;
                Card selectedCard = null;
                List<Card> cards = null;

                // Handle "status" event (game progress)
                connection.On<string>("status", async (status) =>
                {
                    Console.WriteLine($"User {userIndex}: SignalR status = {status}");

                    if (status == "distribution_in_progress" && !cardSelected)
                    {
                        // Step 4: Get Cards
                        Console.WriteLine($"User {userIndex}: Getting cards...");
                        bool foundUniqueCards = false;
                        int maxRetries = 10;
                        int retryCount = 0;

                        var randomTime = new Random();
                        var randomDelayTime = randomTime.Next(CARD_DELAY); // Random delay up to 1 hour

                        // Use Task.Delay instead of setTimeout
                        await Task.Delay(randomDelayTime);

                        while (!foundUniqueCards && retryCount < maxRetries)
                        {
                            try
                            {
                                Console.WriteLine($"User {userIndex}: Attempt {retryCount + 1} to get unique cards...");
                                var cardsResponse = await httpClient.GetAsync($"{BASE_URL}/api/Card");
                                cardsResponse.EnsureSuccessStatusCode();

                                var cardsResponseBody = await cardsResponse.Content.ReadAsStringAsync();
                                cards = JsonConvert.DeserializeObject<List<Card>>(cardsResponseBody);

                                if (cards == null || cards.Count == 0)
                                {
                                    throw new Exception("Received empty or null card list");
                                }

                                Console.WriteLine($"User {userIndex}: Got cards ids {string.Join(", ", cards.Select(c => c.Id))}");
                                // Check if these cards have been assigned to other users
                                bool hasDuplicateCards = false;
                                string duplicateDetails = "";
                                Dictionary<int, int> tempCardAssignments = new Dictionary<int, int>();

                                lock (cardMapLock)
                                {
                                    foreach (var card in cards ?? new List<Card>())
                                    {
                                        if (cardIdToUserMap.TryGetValue(card.Id, out int existingUserId))
                                        {
                                            hasDuplicateCards = true;
                                            duplicateDetails += $"Card {card.Id} already assigned to user {existingUserId}. ";
                                        }
                                        else
                                        {
                                            // Track this card as a potential assignment
                                            tempCardAssignments[card.Id] = userId;
                                        }
                                    }

                                    if (!hasDuplicateCards)
                                    {
                                        // No duplicates found, record these cards as assigned to this user
                                        foreach (var entry in tempCardAssignments)
                                        {
                                            cardIdToUserMap[entry.Key] = entry.Value;
                                        }
                                        foundUniqueCards = true;
                                    }
                                    else
                                    {
                                        retryCount++;
                                        Console.WriteLine($"WARNING: User {userIndex} with user ID {userId}: Received duplicate cards! {duplicateDetails} Retrying ({retryCount}/{maxRetries})...");
                                        hasDuplicateCards = true; // Set flag to use outside lock
                                    }
                                }

                                // Wait a bit before retrying if duplicates were found
                                if (hasDuplicateCards)
                                {
                                    await Task.Delay(10);
                                }
                            }
                            catch (Exception ex)
                            {
                                retryCount++;
                                Console.WriteLine($"User {userIndex}: Error getting cards: {ex.Message}. Retrying ({retryCount}/{maxRetries})...");
                                await Task.Delay(50); // Wait a bit longer after an error
                            }
                        }

                        if (!foundUniqueCards)
                        {
                            Console.WriteLine($"ERROR: User {userIndex} with user ID {userId}: Could not get unique cards after {maxRetries} retries. Proceeding with potentially duplicate cards.");

                            // As a last resort, record these cards as assigned to this user
                            lock (cardMapLock)
                            {
                                foreach (var card in cards ?? new List<Card>())
                                {
                                    cardIdToUserMap[card.Id] = userId;
                                }
                            }
                        }

                        Console.WriteLine($"User {userIndex} with user ID {userId}: Got {cards?.Count ?? 0} cards with ids: {string.Join(", ", cards?.Select(c => c.Id) ?? new List<int>())}");

                        if (cards?.Count > 0)
                        {
                            var selectedId = cards[0].Id; // Select the first card for simplicity
                            var random = new Random();
                            var randomDelay = random.Next(SELECT_DELAY); // Random delay up to 1 hour

                            // Use Task.Delay instead of setTimeout
                            await Task.Delay(randomDelay);

                            try
                            {
                                var selectCardData = new { id = selectedId };
                                var selectCardContent = new StringContent(
                                    JsonConvert.SerializeObject(selectCardData),
                                    Encoding.UTF8,
                                    "application/json"
                                );

                                var selectCardResponse = await httpClient.PostAsync($"{BASE_URL}/api/Card/Select", selectCardContent);
                                selectCardResponse.EnsureSuccessStatusCode();

                                Console.WriteLine($"User {userIndex}: Card selected after {randomDelay}ms");
                                Interlocked.Increment(ref notif);
                                cardSelected = true;
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"User {userIndex}: Failed to select card: {ex.Message}");
                            }
                        }
                    }

                    if (status == "emission_in_progress")
                    {
                        selectedCard = cards[0];
                    }
                });

                // Handle "NumberSelected" event (user receives notification of a number to select)
                connection.On<int>("NumberSelected", (number) =>
                {
                    if (selectedCard == null) return;

                    // Check if this number is in the user's card
                    var flatNumbers = selectedCard.Cards.SelectMany(row => row).ToList();

                    if (flatNumbers.Contains(number))
                    {
                        Console.WriteLine($"User {userIndex}: Number {number} is in card");

                        // Store the number to be selected during the Timer event
                        lock (selectedNumbersLock)
                        {
                            // Initialize the set if it doesn't exist for this user
                            if (!userSelectedNumbers.ContainsKey(userId))
                            {
                                userSelectedNumbers[userId] = new HashSet<int>();
                            }

                            // Check if this number has already been selected by this user
                            if (userSelectedNumbers[userId].Contains(number))
                            {
                                Console.WriteLine($"User {userIndex}: Number {number} was already selected by this user");
                            }
                            else
                            {
                                // Store the number as pending selection
                                if (!pendingNumberSelections.ContainsKey(userId))
                                {
                                    pendingNumberSelections[userId] = new HashSet<int>();
                                }
                                pendingNumberSelections[userId].Add(number);
                                Console.WriteLine($"User {userIndex}: Number {number} queued for selection during Timer event");
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine($"User {userIndex}: Number {number} not in card");
                    }
                });

                // Handle "Timer" event (game countdown)
                connection.On<int>("Timer", async (timeLeft) =>
                {
                    Console.WriteLine($"User {userIndex}: Timer = {timeLeft}");

                    // Only attempt to select numbers when we have a selected card
                    if (selectedCard != null)
                    {
                        // Use timeLeft to distribute the load
                        // For example, some users will select at timeLeft = 10, others at 9, etc.
                        // This creates a more natural distribution based on the user's index
                        int userSpecificTriggerTime = (userIndex % MAX_TIMER) + 1; // Distribute across 1-5 seconds

                        if (timeLeft == userSpecificTriggerTime)
                        {
                            // Process any pending number selections
                            HashSet<int> pendingNumbers = null;

                            lock (selectedNumbersLock)
                            {
                                // Check if there are any pending numbers to select
                                if (pendingNumberSelections.TryGetValue(userId, out pendingNumbers) && pendingNumbers.Count > 0)
                                {
                                    // Make a copy of the pending numbers to process outside the lock
                                    pendingNumbers = new HashSet<int>(pendingNumbers);
                                    // Clear the pending selections as we're about to process them
                                    pendingNumberSelections[userId].Clear();
                                }
                            }

                            if (pendingNumbers != null && pendingNumbers.Count > 0)
                            {
                                var random = new Random();
                                var randomDelay = random.Next(100); // Random delay up to 100ms

                                await Task.Delay(randomDelay);

                                foreach (int numberToSelect in pendingNumbers)
                                {
                                    // Check if this number has already been selected
                                    bool alreadySelected = false;

                                    lock (selectedNumbersLock)
                                    {
                                        if (!userSelectedNumbers.ContainsKey(userId))
                                        {
                                            userSelectedNumbers[userId] = new HashSet<int>();
                                        }

                                        alreadySelected = userSelectedNumbers[userId].Contains(numberToSelect);
                                    }

                                    if (!alreadySelected)
                                    {
                                        int score = 10; // or based on some logic

                                        try
                                        {
                                            var numberData = new[] { numberToSelect, score };
                                            var numberContent = new StringContent(
                                                JsonConvert.SerializeObject(numberData),
                                                Encoding.UTF8,
                                                "application/json"
                                            );

                                            var numberResponse = await httpClient.PostAsync($"{BASE_URL}/api/SelectedNumberClient/Number", numberContent);
                                            numberResponse.EnsureSuccessStatusCode();

                                            // Mark this number as selected for this user
                                            lock (selectedNumbersLock)
                                            {
                                                userSelectedNumbers[userId].Add(numberToSelect);
                                            }

                                            Console.WriteLine($"User {userIndex}: Sent selected number {numberToSelect} at timeLeft={timeLeft} after {randomDelay}ms");
                                            Interlocked.Increment(ref notif);
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine($"User {userIndex}: Error sending selected number: {ex.Message}");
                                        }
                                    }
                                    else
                                    {
                                        Console.WriteLine($"User {userIndex}: Number {numberToSelect} already selected, skipping");
                                    }
                                }
                            }
                        }
                    }
                });

                // Step 6: Start SignalR connection
                await connection.StartAsync();
                Console.WriteLine($"User {userIndex}: SignalR connection started");

                // Keep the connection alive for the simulation
                await Task.Delay(TimeSpan.FromHours(24));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"User {userIndex} error: {ex.Message}");
                Interlocked.Increment(ref fail);
            }
        }
    }

    // Data models
    public class UserToken
    {
        [JsonProperty("userId")]
        public int UserId { get; set; }

        [JsonProperty("accessToken")]
        public string AccessToken { get; set; }
    }

    public class TokenData
    {
        [JsonProperty("accessToken")]
        public string AccessToken { get; set; }
    }

    public class Card
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("cards")]
        public List<List<int>> Cards { get; set; }
    }
}