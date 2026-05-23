# Bibliotheekverkennen (nl-NL)

```mermaid
graph TD
    Idle["Inactief"] --> Browse["BrowseLibraryIntent<br/>browse {browse_category}<br/>welke {browse_category} zijn er"]]

    Browse -->|"blader door artiesten"| Artists["Artiestenlijst"]
    Browse -->|"blader door albums"| Albums["Albumlijst"]
    Browse -->|"blader door nummers"| Songs["Nummerlijst"]
    Browse -->|"blader door films"| Movies["Filmlist"]
    Browse -->|"blader door series"| Series["Seriellijst"]
    Browse -->|"blader door boeken"| Books["Boekenlijst"]
    Browse -->|"blader door genres"| Genres["Genreslijst"]

    Artists --> Selection["Gebruiker kiest een item"]
    Albums --> Selection
    Songs --> Selection
    Movies --> Selection
    Series --> Selection
    Books --> Selection
    Genres --> Selection

    Selection --> PlayItem["Geselecteerd item afspelen"]

    Idle --> Favorites["PlayFavoritesIntent<br/>speel mijn favorieten"]
    Favorites --> PlayFav["Favorieten-afspeellijst afspelen"]

    Idle --> RecentlyPlayed["InProgressMediaListIntent<br/>waar was ik naar aan het luisteren"]
    RecentlyPlayed --> ProgressList["Lijst met lopende media"]
    ProgressList --> Selection

    Idle --> LastAdded["PlayLastAddedIntent<br/>speel nieuwe media"]
    LastAdded --> RecentList["Recent toegevoegde items"]
    RecentList --> Selection

    Idle --> RecentQuery["QueryRecentlyAddedIntent<br/>wat is er nieuw"]
    RecentQuery --> RecentList

    Idle --> Recommend["RecommendIntent<br/>beveel iets aan"]
    Recommend --> RecList["Aanbevolen items"]
    RecList --> Selection

    Idle --> PlayBook["PlayBookIntent<br/>play {book}"]
    PlayBook --> Audiobook["Audioboek afspelen"]
    Audiobook -->|"volgend hoofdstuk"| ChapterNext["GoToChapterIntent"]
    Audiobook -->|"vorig hoofdstuk"| ChapterPrev["GoToChapterIntent"]
    Audiobook -->|"ga naar hoofdstuk {n}"| ChapterNum["GoToChapterIntent"]

    Idle --> PlayPodcast["PlayPodcastIntent<br/>speel de podcast {podcast_name}"]
    PlayPodcast --> PodcastPlay["Podcast afspelen"]

    Idle --> ContinueWatch["ContinueWatchingIntent<br/>verder kijken"]
    ContinueWatch --> ResumePlayback["Lopende media hervatten"]

    Idle --> SearchMedia["SearchMediaIntent<br/>zoek naar {query}"]
    SearchMedia --> SearchResults["Zoekresultaten"]
    SearchResults --> Selection

    style PlayItem fill:#4CAF50,color:#fff
    style Audiobook fill:#9C27B0,color:#fff
    style PodcastPlay fill:#9C27B0,color:#fff
    style Browse fill:#2196F3,color:#fff
```
