# Navigation de la médiathèque (fr-CA)

```mermaid
graph TD
    Idle["Inactif"] --> Browse["BrowseLibraryIntent<br/>parcourir {browse_category}<br/>quels {browse_category} y a-t-il"]]

    Browse -->|"parcourir les artistes"| Artists["Liste des artistes"]
    Browse -->|"parcourir les albums"| Albums["Liste des albums"]
    Browse -->|"parcourir les chansons"| Songs["Liste des chansons"]
    Browse -->|"parcourir les films"| Movies["Liste des films"]
    Browse -->|"parcourir les séries"| Series["Liste des séries"]
    Browse -->|"parcourir les livres"| Books["Liste des livres"]
    Browse -->|"parcourir les genres"| Genres["Liste des genres"]

    Artists --> Selection["L'utilisateur choisit un élément"]
    Albums --> Selection
    Songs --> Selection
    Movies --> Selection
    Series --> Selection
    Books --> Selection
    Genres --> Selection

    Selection --> PlayItem["Lire l'élément sélectionné"]

    Idle --> Favorites["PlayFavoritesIntent<br/>Lis mes favoris"]
    Favorites --> PlayFav["Lire la playlist des favoris"]

    Idle --> RecentlyPlayed["InProgressMediaListIntent<br/>qu'est-ce que j'écoute"]
    RecentlyPlayed --> ProgressList["Liste des médias en cours"]
    ProgressList --> Selection

    Idle --> LastAdded["PlayLastAddedIntent<br/>Lis les nouveaux médias"]
    LastAdded --> RecentList["Éléments ajoutés récemment"]
    RecentList --> Selection

    Idle --> RecentQuery["QueryRecentlyAddedIntent<br/>quoi de neuf"]
    RecentQuery --> RecentList

    Idle --> Recommend["RecommendIntent<br/>recommande quelque chose"]
    Recommend --> RecList["Éléments recommandés"]
    RecList --> Selection

    Idle --> PlayBook["PlayBookIntent<br/>lis {book}"]
    PlayBook --> Audiobook["Lecture de livre audio"]
    Audiobook -->|"Chapitre suivant"| ChapterNext["GoToChapterIntent"]
    Audiobook -->|"Chapitre précédent"| ChapterPrev["GoToChapterIntent"]
    Audiobook -->|"Aller au chapitre {n}"| ChapterNum["GoToChapterIntent"]

    Idle --> PlayPodcast["PlayPodcastIntent<br/>joue le podcast {podcast_name}"]
    PlayPodcast --> PodcastPlay["Lecture de podcast"]

    Idle --> ContinueWatch["ContinueWatchingIntent<br/>Continuer à regarder"]
    ContinueWatch --> ResumePlayback["Reprendre les médias en cours"]

    Idle --> SearchMedia["SearchMediaIntent<br/>Cherche {query}"]
    SearchMedia --> SearchResults["Résultats de recherche"]
    SearchResults --> Selection

    style PlayItem fill:#4CAF50,color:#fff
    style Audiobook fill:#9C27B0,color:#fff
    style PodcastPlay fill:#9C27B0,color:#fff
    style Browse fill:#2196F3,color:#fff
```
