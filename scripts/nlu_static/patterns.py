"""The sample PATTERN/WITNESS model and the matcher core (JF-954.4).

A PATTERN is one intent sample turned into a sequence of items: literal
tokens (the shared ``tokenize`` over the text between ``{slot}``
placeholders) and slot items, each carrying the class from
``classify_slot_type``. A WITNESS is the abstract utterance a sample
generates: the same sequence with each slot collapsed into one class
token. The DP in this module answers "can pattern P consume the whole of
witness W" and, with the utterance mode, "can P consume a real
utterance's tokens".

The rule tables of the two modes are the JF-954.4 contract, implemented
here verbatim and NOT translated to ``re`` patterns (vocabulary
alternations blow up).

Items are tuples, frozen dataclasses would be slower at this call
volume: a literal item is ``("lit", token)``; a slot item is
``("slot", class, type_name)``; a witness class token is
``("cls", label)`` where the label is ``FREE``, ``NUM``,
``STRUCT:<type>`` or ``CLOSED:<type>``.
"""

from __future__ import annotations

from dataclasses import dataclass

from nlu_static.effective_model import EffectiveModel, SlotTypeVocab, classify_slot_type, entry_texts
from nlu_static.normalize import SLOT_PLACEHOLDER_RE, tokenize

# Slot item classes, exactly the classify_slot_type vocabulary plus the
# UNKNOWN_BUILTIN downgrade (treated as FREE, reported by the caller).
CLASS_FREE = "FREE"
CLASS_NUMERIC = "NUMERIC"
CLASS_STRUCTURED = "STRUCTURED"
CLASS_CLOSED = "CLOSED"
CLASS_UNKNOWN = "UNKNOWN_BUILTIN"

WitnessMode = "witness"
UtteranceMode = "utterance"


def _class_label(cls: str, type_name: str) -> str:
    """The witness class token label for one slot item."""
    if cls in (CLASS_FREE, CLASS_UNKNOWN):
        return "FREE"
    if cls == CLASS_NUMERIC:
        return "NUM"
    if cls == CLASS_STRUCTURED:
        return f"STRUCT:{type_name}"
    return f"CLOSED:{type_name}"


@dataclass(frozen=True)
class CompiledPattern:
    """One sample compiled for matching.

    ``pattern`` holds the lit/slot items; ``witness`` the same sequence
    with slots collapsed to class tokens. ``skeleton`` is the class-only
    key (slots compared by class and, for CLOSED, by type) the graph's
    IDENTICAL relation and the per-intent de-duplication both use.
    """

    intent: str
    sample: str
    pattern: tuple[tuple, ...]
    witness: tuple[tuple, ...]
    skeleton: tuple[tuple, ...]

    @property
    def specificity(self) -> int:
        """The number of literal tokens in the sample (the matcher's ranking)."""
        return sum(1 for item in self.pattern if item[0] == "lit")

    @property
    def first_item(self) -> tuple | None:
        return self.pattern[0] if self.pattern else None

    @property
    def last_item(self) -> tuple | None:
        return self.pattern[-1] if self.pattern else None


def _type_of_slot(name: str, slots: tuple) -> str:
    """The declared type of one slot name, or the empty string."""
    for slot in slots:
        if slot.name == name:
            return slot.type
    return ""


def _entry_token_lists(vocab: SlotTypeVocab) -> frozenset[tuple[str, ...]]:
    """The token tuples of every entry text of one type (non-empty only)."""
    lists: set[tuple[str, ...]] = set()
    for text in entry_texts(vocab):
        tokens = tuple(tokenize(text))
        if tokens:
            lists.add(tokens)
    return frozenset(lists)


def closed_entry_tokens(model: EffectiveModel) -> dict[str, frozenset[tuple[str, ...]]]:
    """The per-type entry token lists every CLOSED matching rule needs.

    Built once per model run (the vocabularies are normalized and
    tokenized once, not once per sample pair); callers thread this dict
    into ``matches``.
    """
    return {vocab.name: _entry_token_lists(vocab) for vocab in model.types}


def compile_model_patterns(model: EffectiveModel) -> tuple[list[CompiledPattern], set[str]]:
    """Compile every sample of every intent of one effective model.

    Returns the compiled patterns in intent-name then sample order, and
    the set of UNKNOWN_BUILTIN type names met along the way (treated as
    FREE; the caller reports them in ``unclassified_types``).
    """
    unclassified: set[str] = set()
    compiled: list[CompiledPattern] = []
    for intent in model.intents:
        for sample in intent.samples:
            compiled.append(compile_pattern(sample, intent, model, unclassified))
    return compiled, unclassified


def compile_pattern(
    sample: str,
    intent,
    model: EffectiveModel,
    unclassified: set[str] | None = None,
) -> CompiledPattern:
    """Compile one sample of one intent into pattern plus witness.

    Literal segments are tokenized with the shared ``tokenize``; each
    placeholder resolves through the intent's declared slot to a class
    from ``classify_slot_type``. An UNKNOWN_BUILTIN class becomes FREE
    for behavior and its type name lands in ``unclassified`` when that
    set is given.
    """
    pattern: list[tuple] = []
    skeleton: list[tuple] = []
    witness: list[tuple] = []
    cursor = 0
    for match in SLOT_PLACEHOLDER_RE.finditer(sample):
        literal = sample[cursor:match.start()]
        for token in tokenize(literal):
            pattern.append(("lit", token))
            skeleton.append(("lit", token))
            witness.append(("lit", token))
        name = match.group(0)[1:-1]
        type_name = _type_of_slot(name, intent.slots)
        cls = classify_slot_type(type_name, model)
        if cls == CLASS_UNKNOWN:
            if unclassified is not None:
                unclassified.add(type_name)
            cls = CLASS_FREE
        pattern.append(("slot", cls, type_name))
        skeleton.append(("slot", cls, type_name) if cls == CLASS_CLOSED else ("slot", cls))
        witness.append(("cls", _class_label(cls, type_name)))
        cursor = match.end()
    for token in tokenize(sample[cursor:]):
        pattern.append(("lit", token))
        skeleton.append(("lit", token))
        witness.append(("lit", token))
    return CompiledPattern(
        intent=intent.name,
        sample=sample,
        pattern=tuple(pattern),
        witness=tuple(witness),
        skeleton=tuple(skeleton),
    )


def matches(
    compiled: CompiledPattern,
    witness: tuple[tuple, ...],
    entry_tokens: dict[str, frozenset[tuple[str, ...]]],
    mode: str = WitnessMode,
    memo: dict[tuple[int, int], bool] | None = None,
) -> bool:
    """True when ``compiled.pattern`` can consume the whole of ``witness``.

    ``mode`` picks the rule table: the WITNESS mode compares a compiled
    sample against another sample's abstract witness; the UTTERANCE mode
    consumes a real utterance's plain tokens, where NUMERIC and
    STRUCTURED deliberately over-approximate (any one token, any one to
    four tokens) and CLOSED matches literal entry runs only. Memoized DP
    over (pattern index, witness index); never a regex. Pass ``memo``
    to share the DP table across several patterns matched against the
    SAME witness (the graph's per-B loop does exactly that).
    """
    pattern = compiled.pattern
    pattern_len = len(pattern)
    witness_len = len(witness)
    if memo is None:
        memo = {}
    # Memo keys carry the compiled pattern's identity: one shared memo
    # may serve several patterns against the same witness, and (pi, wi)
    # alone would collide across them.
    pattern_id = id(compiled)

    def match_from(pi: int, wi: int) -> bool:
        key = (pattern_id, pi, wi)
        cached = memo.get(key)
        if cached is not None:
            return cached
        if pi == pattern_len:
            result = wi == witness_len
            memo[key] = result
            return result
        item = pattern[pi]
        if item[0] == "lit":
            result = (
                wi < witness_len
                and witness[wi] == ("lit", item[1])
                and match_from(pi + 1, wi + 1)
            )
        else:
            cls = item[1]
            if cls == CLASS_FREE:
                result = False
                # one or more consecutive items of ANY kind
                for take in range(wi + 1, witness_len + 1):
                    if match_from(pi + 1, take):
                        result = True
                        break
            elif cls == CLASS_NUMERIC:
                if mode == WitnessMode:
                    result = (
                        wi < witness_len
                        and witness[wi] == ("cls", "NUM")
                        and match_from(pi + 1, wi + 1)
                    )
                else:
                    # utterance over-approximation: exactly one token
                    result = wi < witness_len and match_from(pi + 1, wi + 1)
            elif cls == CLASS_STRUCTURED:
                if mode == WitnessMode:
                    result = (
                        wi < witness_len
                        and witness[wi] == ("cls", f"STRUCT:{item[2]}")
                        and match_from(pi + 1, wi + 1)
                    )
                else:
                    # utterance over-approximation: one to four tokens
                    result = False
                    for take in range(wi + 1, min(wi + 4, witness_len) + 1):
                        if match_from(pi + 1, take):
                            result = True
                            break
            else:  # CLOSED
                type_name = item[2]
                entries = entry_tokens.get(type_name, frozenset())
                result = False
                if mode == WitnessMode and wi < witness_len and witness[wi][0] == "cls":
                    label = witness[wi][1]
                    if label == f"CLOSED:{type_name}":
                        result = match_from(pi + 1, wi + 1)
                    elif label.startswith("CLOSED:"):
                        other = label[len("CLOSED:"):]
                        if entries and entries & entry_tokens.get(other, frozenset()):
                            result = match_from(pi + 1, wi + 1)
                if not result:
                    # literal run equal to one entry's token list
                    lengths = {len(tokens) for tokens in entries}
                    for length in sorted(lengths):
                        if length == 0 or wi + length > witness_len:
                            continue
                        if all(witness[k][0] == "lit" for k in range(wi, wi + length)):
                            run = tuple(witness[k][1] for k in range(wi, wi + length))
                            if run in entries and match_from(pi + 1, wi + length):
                                result = True
                                break
        memo[key] = result
        return result

    return match_from(0, 0)


def skeleton_equal(a: CompiledPattern, b: CompiledPattern) -> bool:
    """True when two samples have identical skeletons (slots by class, CLOSED by type)."""
    return a.skeleton == b.skeleton
