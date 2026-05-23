# Bibliotheksdurchsuchung (de-DE)

```mermaid
graph TD
    Idle["Leerlauf"] --> Browse["BrowseLibraryIntent<br/>durchsuche {browse_category}<br/>welche {browse_category} gibt es"]]

    Browse -->|"durchsuche Künstler"| Artists["Künstlerliste"]
    Browse -->|"durchsuche Alben"| Albums["Albenliste"]
    Browse -->|"durchsuche Lieder"| Songs["Liederliste"]
    Browse -->|"durchsuche Filme"| Movies["Filmliste"]
    Browse -->|"durchsuche Serien"| Series["Serienliste"]
    Browse -->|"durchsuche Bücher"| Books["Bücherliste"]
    Browse -->|"durchsuche Genres"| Genres["Genreliste"]

    Artists --> Selection["Nutzer wählt ein Element"]
    Albums --> Selection
    Songs --> Selection
    Movies --> Selection
    Series --> Selection
    Books --> Selection
    Genres --> Selection

    Selection --> PlayItem["Ausgewähltes Element abspielen"]

    Idle --> Favorites["PlayFavoritesIntent<br/>Spiele meine Favoriten"]
    Favorites --> PlayFav["Favoriten-Wiedergabeliste abspielen"]

    Idle --> RecentlyPlayed["InProgressMediaListIntent<br/>was höre ich gerade"]
    RecentlyPlayed --> ProgressList["Liste laufender Medien"]
    ProgressList --> Selection

    Idle --> LastAdded["PlayLastAddedIntent<br/>Spiele neue Medien"]
    LastAdded --> RecentList["Kürzlich hinzugefügte Elemente"]
    RecentList --> Selection

    Idle --> RecentQuery["QueryRecentlyAddedIntent<br/>was ist neu"]
    RecentQuery --> RecentList

    Idle --> Recommend["RecommendIntent<br/>empfehle etwas"]
    Recommend --> RecList["Empfohlene Elemente"]
    RecList --> Selection

    Idle --> PlayBook["PlayBookIntent<br/>spiele {book}"]
    PlayBook --> Audiobook["Hörbuch-Wiedergabe"]
    Audiobook -->|"Nächstes Kapitel"| ChapterNext["GoToChapterIntent"]
    Audiobook -->|"Vorheriges Kapitel"| ChapterPrev["GoToChapterIntent"]
    Audiobook -->|"Gehe zu Kapitel {n}"| ChapterNum["GoToChapterIntent"]

    Idle --> PlayPodcast["PlayPodcastIntent<br/>Spiele den Podcast {podcast_name}"]
    PlayPodcast --> PodcastPlay["Podcast-Wiedergabe"]

    Idle --> ContinueWatch["ContinueWatchingIntent<br/>Weiter schauen"]
    ContinueWatch --> ResumePlayback["Laufendes Medium fortsetzen"]

    Idle --> SearchMedia["SearchMediaIntent<br/>Suche nach {query}"]
    SearchMedia --> SearchResults["Suchergebnisse"]
    SearchResults --> Selection

    style PlayItem fill:#4CAF50,color:#fff
    style Audiobook fill:#9C27B0,color:#fff
    style PodcastPlay fill:#9C27B0,color:#fff
    style Browse fill:#2196F3,color:#fff
```
