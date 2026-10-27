"""Read EX_NameConst using the client layout verified in RULE-483.

The bytecode stores comparison/display/number uint32 values. The current
client handler returns comparison/number and advances the cursor by twelve
payload bytes. Display is retained here for evidence, not used as a number.
This is an audit decoder helper; it does not execute game bytecode.
"""
from typing import NamedTuple
import struct


class ScriptNameConstant(NamedTuple):
    comparison: int
    display: int
    number: int
    end: int


def read_name_const(raw: bytes, offset: int) -> ScriptNameConstant:
    if offset < 0 or offset + 13 > len(raw):
        raise ValueError("truncated EX_NameConst")
    if raw[offset] != 0x21:
        raise ValueError("expected EX_NameConst opcode21")
    comparison, display, number = struct.unpack_from("<III", raw, offset + 1)
    return ScriptNameConstant(comparison, display, number, offset + 13)
