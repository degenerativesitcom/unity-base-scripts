using MongoDB.Bson;
using MongoDB.Driver;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using TMPro;
using Cinemachine;
using System.IO;
using UnityEngine.SceneManagement;

public class CharacterDialogueManagerBase : MonoBehaviour
{
    [Header("UI and Character Components")]
    public List<TextMeshProUGUI> characterTexts;
    public List<AudioSource> characterAudioSources;
    public List<Transform> characterTransforms;
    public CinemachineVirtualCamera virtualCamera;
    public string audioFolderPath = "C:/path";

    [Header("Additional UI Elements")]
    public TextMeshProUGUI infoText; 

    [Header("Database Configurations")]
    private MongoClient client;
    private IMongoDatabase database;
    private IMongoCollection<BsonDocument> scenariosCollection;
    public string mongoConnectionString = "mongodb+srv://.........................";
    public string databaseName = "SCENARIO";
    public string collectionName = "generated_scenario";

    private Queue<DialogueLine> dialogueQueue = new Queue<DialogueLine>();
    private List<BsonDocument> scenarioList = new List<BsonDocument>();
    private bool isProcessingScenario = false;
    private bool isScenariosFetched = false;
    private bool isCheckingForNewScenarios = false;  

    public List<string> sceneNames;
    public LoadingScreen loadingScreen;


    [Header("Text Typing Settings")]
    public float scrollSpeed = 0.05f;
    public int maxVisibleCharacters = 50;
    public class DialogueLine
    {
        public string character;
        public string text;
        public string audioPath;
    }

    [Header("Final Audio Clips and Sources")]
    public List<AudioClip> finalAudioClips;  
    public List<AudioSource> finalAudioSources;  

    [Header("Waiting")]
    public List<Animator> characterAnimators;  
    public string checkingForNewScenariosLayerName = "CheckingLayer";
    public string defaultLayerName = "DefaultLayer";
    public AudioSource backgroundAudioSource;  
    public AudioClip searchingAudioClip;      

    void Start()
    {
        client = new MongoClient(mongoConnectionString);
        database = client.GetDatabase(databaseName);
        scenariosCollection = database.GetCollection<BsonDocument>(collectionName);

        StartCoroutine(FetchScenarios());
    }

IEnumerator FetchScenarios()
{
    try
    {

        var filter = Builders<BsonDocument>.Filter.And(
            Builders<BsonDocument>.Filter.Eq("processed", false),
            Builders<BsonDocument>.Filter.Eq("unload", true)
        );

        scenarioList = scenariosCollection.Find(filter)
                        .Sort(Builders<BsonDocument>.Sort.Ascending("generation_time"))
                        .ToList();

        if (scenarioList.Count > 0)
        {
            isScenariosFetched = true;

            isCheckingForNewScenarios = false;
            StopCoroutine("PeriodicallyCheckForNewScenarios");
        }
        else
        {
            Debug.LogWarning("Сценарии не найдены или все уже обработаны.");
        }
    }
    catch (System.Exception ex)
    {
        Debug.LogError($"Ошибка загрузки сценариев из MongoDB: {ex.Message}");
    }
    yield return null;
}


void Update()
{

    if (isScenariosFetched && !isProcessingScenario && !isCheckingForNewScenarios && scenarioList.Count > 0)
    {
        StartCoroutine(ProcessNextScenario());
    }


    if (scenarioList.Count == 0 && !isProcessingScenario && !isCheckingForNewScenarios)
    {

        if (isCheckingForNewScenarios)
        {
            StopCoroutine("PeriodicallyCheckForNewScenarios");
        }
        StartCoroutine(PeriodicallyCheckForNewScenarios());
    }
}


    IEnumerator ProcessNextScenario()
    {
        isProcessingScenario = true;

        BsonDocument scenario = scenarioList[0];
        scenarioList.RemoveAt(0);

        yield return new WaitForSeconds(0.5f); 

        InitializeWithScenario(scenario);

        yield return new WaitForSeconds(2);

        if (CheckAllAudioFiles(scenario))
        {
            yield return StartCoroutine(PlayScenario());

            UpdateScenarioAsProcessed(scenario["_id"].AsObjectId);


        bool statusUpdated = false;
        while (!statusUpdated)
        {
            var updatedScenario = scenariosCollection.Find(Builders<BsonDocument>.Filter.Eq("_id", scenario["_id"].AsObjectId))
                                                     .FirstOrDefault();
            statusUpdated = updatedScenario != null && updatedScenario.GetValue("processed", false).AsBoolean;
            yield return new WaitForSeconds(0.2f);  
        }

            yield return StartCoroutine(PlayFinalAudioClipsBeforeScenarioLoad());
        }
        else
        {
            Debug.LogError($"Один или несколько аудиофайлов для сценария с темой '{scenario.GetValue("topic", "").AsString}' отсутствуют.");
        }

        isProcessingScenario = false;
    }

    IEnumerator PlayFinalAudioClipsBeforeScenarioLoad()
    {
        for (int i = 0; i < finalAudioClips.Count; i++)
        {
            if (finalAudioSources[i] != null && finalAudioClips[i] != null)
            {
                finalAudioSources[i].clip = finalAudioClips[i];
                finalAudioSources[i].Play();
            }
        }

        bool allClipsFinished;
        do
        {
            allClipsFinished = true;
            foreach (var source in finalAudioSources)
            {
                if (source != null && source.isPlaying)
                {
                    allClipsFinished = false;
                    break;
                }
            }
            yield return null;
        } while (!allClipsFinished);


        LoadNextScene();
    }

    void UpdateScenarioAsProcessed(ObjectId scenarioId)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("_id", scenarioId);
        var update = Builders<BsonDocument>.Update.Set("processed", true);
        scenariosCollection.UpdateOne(filter, update);
    }

 public void InitializeWithScenario(BsonDocument scenario)
{
    dialogueQueue.Clear();


    string topic = scenario.Contains("topic") && scenario["topic"].IsString 
                   ? scenario["topic"].AsString 
                   : "Unknown Topic";

    string username = scenario.Contains("username") && scenario["username"].IsString 
                      ? scenario["username"].AsString 
                      : "Anonymous";


    if (infoText != null)
    {
        infoText.text = $"User: {username} | Topic: {topic}";
    }

 
    if (scenario.Contains("scenario") && scenario["scenario"].IsBsonArray)
    {
        var lines = scenario["scenario"].AsBsonArray;
        foreach (var line in lines)
        {

            if (line.IsBsonDocument)
            {
                var lineDoc = line.AsBsonDocument;

                string character = lineDoc.Contains("character") && lineDoc["character"].IsString
                                   ? lineDoc["character"].AsString
                                   : "Unknown Character";

                string text = lineDoc.Contains("line") && lineDoc["line"].IsString
                              ? lineDoc["line"].AsString
                              : "";

                string audioPath = lineDoc.Contains("audio_path") && lineDoc["audio_path"].IsString
                                   ? lineDoc["audio_path"].AsString
                                   : "";

                DialogueLine dialogueLine = new DialogueLine
                {
                    character = character,
                    text = text,
                    audioPath = audioPath
                };

                dialogueQueue.Enqueue(dialogueLine);
            }
        }
    }
    else
    {
        Debug.LogError("Scenario document does not contain a valid 'scenario' array.");
    }
}


    public IEnumerator PlayScenario()
{

    if (backgroundAudioSource != null && backgroundAudioSource.isPlaying)
    {
        backgroundAudioSource.Pause();  
    }

foreach (var animator in characterAnimators)
{
    if (animator != null)
    {
        animator.SetLayerWeight(animator.GetLayerIndex(checkingForNewScenariosLayerName), 0f);
        animator.SetLayerWeight(animator.GetLayerIndex(defaultLayerName), 1f);
    }
}

    while (dialogueQueue.Count > 0)
    {
        DialogueLine currentLine = dialogueQueue.Dequeue();

        UpdateDialogueUI(currentLine);
        MoveCameraToCharacter(currentLine.character);

        string fullAudioPath = Path.Combine(audioFolderPath, currentLine.audioPath.Replace("/", "\\"));
        yield return StartCoroutine(LoadAndPlayAudio(fullAudioPath, currentLine.character));
        yield return new WaitForSeconds(0.2f);
    }

    ClearDialogueUI();


    if (backgroundAudioSource != null)
    {
        backgroundAudioSource.Play();
    }

foreach (var animator in characterAnimators)
{
    if (animator != null)
    {
        animator.SetLayerWeight(animator.GetLayerIndex(checkingForNewScenariosLayerName), 0.5f);
        animator.SetLayerWeight(animator.GetLayerIndex(defaultLayerName), 1f);
    }
}
}

    IEnumerator LoadAndPlayAudio(string filePath, string character)
    {
        if (File.Exists(filePath))
        {
            using (UnityWebRequest www = UnityWebRequestMultimedia.GetAudioClip("file://" + filePath, AudioType.WAV))
            {
                yield return www.SendWebRequest();

                if (www.result == UnityWebRequest.Result.Success)
                {
                    AudioClip clip = DownloadHandlerAudioClip.GetContent(www);
                    AudioSource characterAudioSource = GetAudioSourceForCharacter(character);

                    if (characterAudioSource != null)
                    {
                        characterAudioSource.clip = clip;
                        characterAudioSource.Play();
                        yield return new WaitWhile(() => characterAudioSource.isPlaying);
                        Destroy(clip);
                    }
                }
                else
                {
                    Debug.LogError($"Ошибка загрузки аудио: {www.error}");
                }
            }
        }
        else
        {
            Debug.LogError($"Аудиофайл не найден: {filePath}");
        }
    }

    void MoveCameraToCharacter(string character)
    {
        int characterIndex = GetCharacterIndex(character);
        if (characterIndex >= 0)
        {
            Transform targetTransform = characterTransforms[characterIndex];
            virtualCamera.Follow = targetTransform;
            virtualCamera.LookAt = targetTransform;
        }
    }

    void UpdateDialogueUI(DialogueLine currentLine)
    {
        foreach (var text in characterTexts)
        {
            text.text = "";
        }

        int characterIndex = GetCharacterIndex(currentLine.character);
        if (characterIndex >= 0)
        {
            StartCoroutine(TypeAndScrollText(characterTexts[characterIndex], currentLine.text, maxVisibleCharacters));
        }
    }

    IEnumerator TypeAndScrollText(TextMeshProUGUI textComponent, string textToType, int maxVisibleCharacters)
    {
        textComponent.text = "";  

        int visibleCharacterCount = 0;

        foreach (char letter in textToType)
        {
            textComponent.text += letter;
            visibleCharacterCount++;

            if (visibleCharacterCount > maxVisibleCharacters)
            {
                textComponent.text = textComponent.text.Substring(1);
            }

            yield return new WaitForSeconds(scrollSpeed);
        }
    }

    void ClearDialogueUI()
    {
        foreach (var text in characterTexts)
        {
            text.text = "";
        }
    }

    int GetCharacterIndex(string character)
    {
        for (int i = 0; i < characterTexts.Count; i++)
        {
            if (characterTexts[i].name == character)
            {
                return i;
            }
        }
        return -1;
    }

    AudioSource GetAudioSourceForCharacter(string character)
    {
        int index = GetCharacterIndex(character);
        return index >= 0 ? characterAudioSources[index] : null;
    }

    bool CheckAllAudioFiles(BsonDocument scenario)
    {
        var lines = scenario["scenario"].AsBsonArray;
        foreach (var line in lines)
        {
            var audioPath = line["audio_path"].AsString;
            string fullAudioPath = Path.Combine(audioFolderPath, audioPath.Replace("/", "\\"));

            if (!File.Exists(fullAudioPath))
            {
                Debug.LogError($"Отсутствует аудиофайл: {fullAudioPath}");
                return false;
            }
        }
        return true;
    }

    void LoadNextScene()
    {   
        StopAllCoroutines();
        if (sceneNames.Count > 0)
        {
            int randomIndex;
            string nextSceneName;


            do
            {
                randomIndex = Random.Range(0, sceneNames.Count);
                nextSceneName = sceneNames[randomIndex];
            } while (nextSceneName == SceneManager.GetActiveScene().name);

            loadingScreen.ShowLoadingScreen(nextSceneName);


            SceneCounter.instance.SceneLoaded();
            

            Resources.UnloadUnusedAssets();
            System.GC.Collect();
        }
        else
        {
            Debug.LogError("Список сцен пуст!");
        }
    }

IEnumerator PeriodicallyCheckForNewScenarios()
{
    isCheckingForNewScenarios = true;
    UnityEngine.Debug.Log("Проверка новых сценариев...");

    if (infoText != null)
    {
        infoText.text = "Waiting for scenario...";  
    }

    foreach (var animator in characterAnimators)
    {
        if (animator != null)
        {
            animator.SetLayerWeight(animator.GetLayerIndex(checkingForNewScenariosLayerName), 0.5f);
            animator.SetLayerWeight(animator.GetLayerIndex(defaultLayerName), 0f);
        }
    }

    if (backgroundAudioSource != null && searchingAudioClip != null)
    {
        backgroundAudioSource.clip = searchingAudioClip;
        backgroundAudioSource.Play();
    }

    float waitTime = 300f;  
    float startTime = Time.time;
    bool scenariosFound = false;

    while (!scenariosFound && Time.time - startTime < waitTime)
    {
        yield return new WaitForSeconds(10);  
        yield return StartCoroutine(FetchScenarios());

        if (isScenariosFetched && scenarioList.Count > 0)
        {
            UnityEngine.Debug.Log("Сценарий найден.");
            scenariosFound = true;  
        }
    }

    if (!scenariosFound)
    {
        UnityEngine.Debug.Log("Время ожидания завершено. Перезагрузка...");
        SceneCounter.instance.RestartApplication(); 
        yield break; 
    }

    if (backgroundAudioSource != null)
    {
        backgroundAudioSource.Stop();
    }

    foreach (var animator in characterAnimators)
    {
        if (animator != null)
        {
            animator.SetLayerWeight(animator.GetLayerIndex(checkingForNewScenariosLayerName), 0f);
            animator.SetLayerWeight(animator.GetLayerIndex(defaultLayerName), 1f);
        }
    }

    if (infoText != null)
    {
        infoText.text = "";  
    }

    UnityEngine.Debug.Log("Новые сценарии найдены, продолжаем обработку.");
    isCheckingForNewScenarios = false;
}




}
