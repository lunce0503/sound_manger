"""Check byte identity apart from PE timestamps and the CodeView PDB GUID; edit no files."""
import hashlib
import json
from pathlib import Path
import struct
import sys

def normalize(original):
    data=bytearray(original)
    assert data[:2]==b"MZ"
    pe=struct.unpack_from("<I",data,0x3c)[0]
    assert data[pe:pe+4]==b"PE\0\0" and struct.unpack_from("<H",data,pe+24)[0]==0x20b
    count=struct.unpack_from("<H",data,pe+6)[0]
    optional=struct.unpack_from("<H",data,pe+20)[0]
    rva,size=struct.unpack_from("<II",data,pe+24+112+6*8)
    data[pe+8:pe+12]=b"\0"*4
    for index in range(count):
        section=pe+24+optional+index*40
        virtual,address,raw,pointer=struct.unpack_from("<IIII",data,section+8)
        if address<=rva<address+max(virtual,raw):
            assert size%28==0
            start=pointer+rva-address
            for offset in range(0,size,28):
                entry=start+offset
                kind=struct.unpack_from("<I",data,entry+12)[0]
                codeview=struct.unpack_from("<I",data,entry+24)[0]
                data[entry+4:entry+8]=b"\0"*4
                if kind==2:
                    assert data[codeview:codeview+4]==b"RSDS"
                    data[codeview+4:codeview+20]=b"\0"*16
    return data

first,second=(Path(argument).read_bytes() for argument in sys.argv[1:])
assert normalize(first)==normalize(second),"Native code/data differs beyond build metadata"
print(json.dumps({"onlyPeTimestampsAndPdbGuidDiffer":first!=second,
    "normalizedSha256":hashlib.sha256(normalize(first)).hexdigest(),"nativeCodeAndDataIdentical":True,
    "testedSha256":hashlib.sha256(first).hexdigest(),"rebuiltSha256":hashlib.sha256(second).hexdigest()},indent=2))
