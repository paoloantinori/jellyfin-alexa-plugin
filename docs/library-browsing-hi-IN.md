# लाइब्रेरी ब्राउज़िंग (hi-IN)

```mermaid
graph TD
    Idle["निष्क्रिय"] --> Browse["BrowseLibraryIntent<br/>{browse_category} ब्राउज़ करो<br/>मेरे पास कौन से {browse_category} हैं"]]

    Browse -->|"कलाकार ब्राउज़ करो"| Artists["कलाकार सूची"]
    Browse -->|"एल्बम ब्राउज़ करो"| Albums["एल्बम सूची"]
    Browse -->|"गाने ब्राउज़ करो"| Songs["गाने सूची"]
    Browse -->|"फिल्में ब्राउज़ करो"| Movies["फिल्में सूची"]
    Browse -->|"श्रृंखला ब्राउज़ करो"| Series["श्रृंखला सूची"]
    Browse -->|"किताबें ब्राउज़ करो"| Books["किताबें सूची"]
    Browse -->|"शैलियाँ ब्राउज़ करो"| Genres["शैलियाँ सूची"]

    Artists --> Selection["उपयोगकर्ता एक आइटम चुनता है"]
    Albums --> Selection
    Songs --> Selection
    Movies --> Selection
    Series --> Selection
    Books --> Selection
    Genres --> Selection

    Selection --> PlayItem["चयनित आइटम चलाओ"]

    Idle --> Favorites["PlayFavoritesIntent<br/>मेरी पसंदीदा चलाओ"]
    Favorites --> PlayFav["पसंदीदा प्लेलिस्ट चलाओ"]

    Idle --> RecentlyPlayed["InProgressMediaListIntent<br/>मैं क्या सुन रहा हूँ"]
    RecentlyPlayed --> ProgressList["जारी मीडिया सूची"]
    ProgressList --> Selection

    Idle --> LastAdded["PlayLastAddedIntent<br/>नया मीडिया चलाओ"]
    LastAdded --> RecentList["हाल ही में जोड़े गए आइटम"]
    RecentList --> Selection

    Idle --> RecentQuery["QueryRecentlyAddedIntent<br/>क्या नया है"]
    RecentQuery --> RecentList

    Idle --> Recommend["RecommendIntent<br/>कुछ सुझाव दो"]
    Recommend --> RecList["सुझाए गए आइटम"]
    RecList --> Selection

    Idle --> PlayBook["PlayBookIntent<br/>play {book}"]
    PlayBook --> Audiobook["ऑडियोबुक प्लेबैक"]
    Audiobook -->|"अगला चैप्टर"| ChapterNext["GoToChapterIntent"]
    Audiobook -->|"पिछला चैप्टर"| ChapterPrev["GoToChapterIntent"]
    Audiobook -->|"चैप्टर {n} पर जाओ"| ChapterNum["GoToChapterIntent"]

    Idle --> PlayPodcast["PlayPodcastIntent<br/>पॉडकास्ट {podcast_name} चलाओ"]
    PlayPodcast --> PodcastPlay["पॉडकास्ट प्लेबैक"]

    Idle --> ContinueWatch["ContinueWatchingIntent<br/>देखना जारी रखो"]
    ContinueWatch --> ResumePlayback["जारी मीडिया फिर से शुरू करो"]

    Idle --> SearchMedia["SearchMediaIntent<br/>{query} खोजो"]
    SearchMedia --> SearchResults["खोज परिणाम"]
    SearchResults --> Selection

    style PlayItem fill:#4CAF50,color:#fff
    style Audiobook fill:#9C27B0,color:#fff
    style PodcastPlay fill:#9C27B0,color:#fff
    style Browse fill:#2196F3,color:#fff
```
