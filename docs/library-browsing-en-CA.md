# Library Browsing (en-CA)

```mermaid
graph TD
    Idle["Idle"] --> Browse["BrowseLibraryIntent<br/>browse {browse_category}<br/>what {browse_category} do i have"]]

    Browse -->|"browse artists"| Artists["Artists list"]
    Browse -->|"browse albums"| Albums["Albums list"]
    Browse -->|"browse songs"| Songs["Songs list"]
    Browse -->|"browse movies"| Movies["Movies list"]
    Browse -->|"browse series"| Series["Series list"]
    Browse -->|"browse books"| Books["Books list"]
    Browse -->|"browse genres"| Genres["Genres list"]

    Artists --> Selection["User picks an item"]
    Albums --> Selection
    Songs --> Selection
    Movies --> Selection
    Series --> Selection
    Books --> Selection
    Genres --> Selection

    Selection --> PlayItem["Play selected item"]

    Idle --> Favorites["PlayFavoritesIntent<br/>Play my favorite {media_type}"]
    Favorites --> PlayFav["Play favorites playlist"]

    Idle --> RecentlyPlayed["InProgressMediaListIntent<br/>what's in progress"]
    RecentlyPlayed --> ProgressList["In-progress media list"]
    ProgressList --> Selection

    Idle --> LastAdded["PlayLastAddedIntent<br/>Play last added {media_type}"]
    LastAdded --> RecentList["Recently added items"]
    RecentList --> Selection

    Idle --> RecentQuery["QueryRecentlyAddedIntent<br/>what's new"]
    RecentQuery --> RecentList

    Idle --> Recommend["RecommendIntent<br/>recommend {media_type}"]
    Recommend --> RecList["Recommended items"]
    RecList --> Selection

    Idle --> PlayBook["PlayBookIntent<br/>play the book {book}"]
    PlayBook --> Audiobook["Audiobook playback"]
    Audiobook -->|"Next chapter"| ChapterNext["GoToChapterIntent"]
    Audiobook -->|"Previous chapter"| ChapterPrev["GoToChapterIntent"]
    Audiobook -->|"Go to chapter {n}"| ChapterNum["GoToChapterIntent"]

    Idle --> PlayPodcast["PlayPodcastIntent<br/>play the podcast {podcast_name}"]
    PlayPodcast --> PodcastPlay["Podcast playback"]

    Idle --> ContinueWatch["ContinueWatchingIntent<br/>Continue watching"]
    ContinueWatch --> ResumePlayback["Resume in-progress media"]

    Idle --> SearchMedia["SearchMediaIntent<br/>Search for {query}"]
    SearchMedia --> SearchResults["Search results"]
    SearchResults --> Selection

    style PlayItem fill:#4CAF50,color:#fff
    style Audiobook fill:#9C27B0,color:#fff
    style PodcastPlay fill:#9C27B0,color:#fff
    style Browse fill:#2196F3,color:#fff
```
