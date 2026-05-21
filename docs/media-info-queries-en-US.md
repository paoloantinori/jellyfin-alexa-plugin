# Media Info Queries (en-US)

```mermaid
graph TD
    Playing["Now Playing"] --> GenericQ["Generic Queries"]
    Playing --> SlotQ["Slot-Specific Queries"]

    GenericQ -->|"what's playing"| TitleResp["Responds with title"]
    GenericQ -->|"what am I listening to"| TitleResp
    GenericQ -->|"what's playing right now"| TitleResp
    GenericQ -->|"what am I watching"| TitleResp
    GenericQ -->|"what is this"| TitleResp
    GenericQ -->|"tell me about this"| FullInfo["Full media info response"]
    GenericQ -->|"information about this"| FullInfo

    SlotQ -->|"What {media_info_type} is this"| SlotResp["Slot-specific response"]
    SlotQ -->|"Tell me the {media_info_type}"| SlotResp
    SlotQ -->|"What is the {media_info_type}"| SlotResp

    SlotQ -->|"What is the title /<br/>What is the name of the song"| Title["title"]
    SlotQ -->|"What album is this from"| Album["album"]
    SlotQ -->|"Who sings this /<br/>Who performs this"| Artist["artist"]
    SlotQ -->|"What year was this released"| Year["year"]
    SlotQ -->|"How long is this /<br/>How long is this song"| Duration["duration"]
    SlotQ -->|"What genre is this"| Genre["genre"]
    SlotQ -->|"Tell me about this artist"| Biography["biography"]
    SlotQ -->|"who directed this /<br/>who is the director"| Director["director"]
    SlotQ -->|"who stars in this /<br/>who is in this"| Cast["cast"]
    SlotQ -->|"what season is this /<br/>which season is this"| Season["season"]
    SlotQ -->|"what episode is this /<br/>which episode is this"| Episode["episode"]
    SlotQ -->|"what show is this"| Series["series"]
    SlotQ -->|"who is the author /<br/>who wrote this"| Author["author"]
    SlotQ -->|"who is the narrator /<br/>who narrates this"| Narrator["narrator"]
    SlotQ -->|"what is the rating /<br/>how is this rated"| Rating["rating"]

    TitleResp --> Playing
    FullInfo --> Playing
    SlotResp --> Playing
    Title --> Playing
    Album --> Playing
    Artist --> Playing
    Year --> Playing
    Duration --> Playing
    Genre --> Playing
    Biography --> Playing
    Director --> Playing
    Cast --> Playing
    Season --> Playing
    Episode --> Playing
    Series --> Playing
    Author --> Playing
    Narrator --> Playing
    Rating --> Playing

    style Playing fill:#4CAF50,color:#fff
    style GenericQ fill:#2196F3,color:#fff
    style SlotQ fill:#FF9800,color:#fff
```
