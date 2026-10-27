"""Classify adjacent CopyData RBX/RDI MOV candidates (RULE-486).

This is syntactic evidence triage, not complete pointer/ownership analysis.
Mentioning [rdi + offset] is insufficient to identify a store: in Intel
syntax it must occur in the destination operand, before the first comma.
"""


def classify_copy_pair(load: dict, following: dict) -> str | None:
    if (load.get("mnemonic") not in {"mov", "movzx", "movups", "movdqu"}
            or "[rbx + " not in load.get("op_str", "")
            or not following.get("mnemonic", "").startswith("mov")
            or "[rdi + " not in following.get("op_str", "")):
        return None
    destination = following["op_str"].split(",", 1)[0]
    return "direct_store" if "[rdi + " in destination else "destination_read"
