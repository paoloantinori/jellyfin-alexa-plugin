# Navegação da Biblioteca (pt-BR)

```mermaid
graph TD
    Idle["Inativo"] --> Browse["BrowseLibraryIntent<br/>navegar {browse_category}<br/>quais {browse_category} existem"]]

    Browse -->|"navegar artistas"| Artists["Lista de artistas"]
    Browse -->|"navegar álbuns"| Albums["Lista de álbuns"]
    Browse -->|"navegar músicas"| Songs["Lista de músicas"]
    Browse -->|"navegar filmes"| Movies["Lista de filmes"]
    Browse -->|"navegar séries"| Series["Lista de séries"]
    Browse -->|"navegar livros"| Books["Lista de livros"]
    Browse -->|"navegar gêneros"| Genres["Lista de gêneros"]

    Artists --> Selection["Usuário escolhe um item"]
    Albums --> Selection
    Songs --> Selection
    Movies --> Selection
    Series --> Selection
    Books --> Selection
    Genres --> Selection

    Selection --> PlayItem["Reproduzir item selecionado"]

    Idle --> Favorites["PlayFavoritesIntent<br/>tocar meus favoritos"]
    Favorites --> PlayFav["Reproduzir playlist de favoritos"]

    Idle --> RecentlyPlayed["InProgressMediaListIntent<br/>o que eu estava ouvindo"]
    RecentlyPlayed --> ProgressList["Lista de mídias em andamento"]
    ProgressList --> Selection

    Idle --> LastAdded["PlayLastAddedIntent<br/>tocar mídias novas"]
    LastAdded --> RecentList["Itis adicionados recentemente"]
    RecentList --> Selection

    Idle --> RecentQuery["QueryRecentlyAddedIntent<br/>o que há de novo"]
    RecentQuery --> RecentList

    Idle --> Recommend["RecommendIntent<br/>recomendar algo"]
    Recommend --> RecList["Itis recomendados"]
    RecList --> Selection

    Idle --> PlayBook["PlayBookIntent<br/>tocar {book}"]
    PlayBook --> Audiobook["Reprodução de audiolivro"]
    Audiobook -->|"próximo capítulo"| ChapterNext["GoToChapterIntent"]
    Audiobook -->|"capítulo anterior"| ChapterPrev["GoToChapterIntent"]
    Audiobook -->|"ir para capítulo {n}"| ChapterNum["GoToChapterIntent"]

    Idle --> PlayPodcast["PlayPodcastIntent<br/>tocar o podcast {podcast_name}"]
    PlayPodcast --> PodcastPlay["Reprodução de podcast"]

    Idle --> ContinueWatch["ContinueWatchingIntent<br/>continuar assistindo"]
    ContinueWatch --> ResumePlayback["Retomar mídia em andamento"]

    Idle --> SearchMedia["SearchMediaIntent<br/>procurar {query}"]
    SearchMedia --> SearchResults["Resultados da busca"]
    SearchResults --> Selection

    style PlayItem fill:#4CAF50,color:#fff
    style Audiobook fill:#9C27B0,color:#fff
    style PodcastPlay fill:#9C27B0,color:#fff
    style Browse fill:#2196F3,color:#fff
```
