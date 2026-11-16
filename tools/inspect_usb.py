"""Read-only PE analysis. Dependencies are loaded from a temporary directory."""
import os, sys, pathlib, re, hashlib, uuid, winreg, subprocess
sys.path.insert(0, os.path.join(os.environ['TEMP'], 'palm-usb-analysis-deps'))
import pefile, capstone
root = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else pathlib.Path(__file__).resolve().parents[2]
store = pathlib.Path('C:/Windows/System32/DriverStore/FileRepository/aceecausbdx64.inf_amd64_28db85c3b4974b9b')
paths = [store/'AceecaUSBDx64.sys', root/'PalmUSBDirect.dll', root/'USBTransport.dll', root/'USB_Driver/USBAPI.dll', root/'Hotsync.exe',root/'USB_Driver/PalmUSBD.sys',pathlib.Path('C:/Windows/SysWOW64/usbport.dll')]
out=[]
for p in paths:
 b=p.read_bytes(); pe=pefile.PE(data=b); base=pe.OPTIONAL_HEADER.ImageBase
 out += [f'## {p}',f'SHA256 {hashlib.sha256(b).hexdigest()} Machine {pe.FILE_HEADER.Machine:#x} ImageBase {base:#x}']
 imports={}
 for d in getattr(pe,'DIRECTORY_ENTRY_IMPORT',[]):
  names=[]
  for i in d.imports:
   n=i.name.decode() if i.name else f'ordinal:{i.ordinal}'; imports[i.address]=n; names.append(n)
  out.append(d.dll.decode()+': '+', '.join(names))
 out.append('Exports: '+', '.join(f'{e.ordinal}:{e.name.decode() if e.name else ""}@{base+e.address:#x}' for e in getattr(getattr(pe,'DIRECTORY_ENTRY_EXPORT',None),'symbols',[])))
 for pattern,encoding in [(rb'[\x20-\x7e]{4,}','ascii'),(rb'(?:[\x20-\x7e]\x00){4,}','utf-16le')]:
  for m in re.finditer(pattern,b):
   s=m.group().decode(encoding)
   if re.search(r'usb|palm|visor|hand|softick|device|ioctl|bulk|pipe|vndr|vendor|connection|guid|winusb|wdf|\\\\|\{[0-9a-f-]{36}\}',s,re.I):out.append(f'file+{m.start():#x} {s}')
 for g in ['784126bf-4190-11d4-b5c2-00c04f687a67','d0b3daa1-d88a-11d2-8a30-00500418785b','a5dcbf10-6530-11d2-901f-00c04fb951ed','784126c0-4190-11d4-b5c2-00c04f687a67']:
  needle=uuid.UUID(g).bytes_le
  for m in re.finditer(re.escape(needle),b):out.append(f'GUID {g} file+{m.start():#x} VA {base+pe.get_rva_from_offset(m.start()):#x}')
 md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64 if pe.FILE_HEADER.Machine==0x8664 else capstone.CS_MODE_32); md.detail=True; md.skipdata=True
 ins=[]
 for s in pe.sections:
  if s.Characteristics & 0x20000000:
   ins += list(md.disasm(s.get_data(),base+s.VirtualAddress))
 lines=[]
 for i in ins:
  annotation=''
  for op in (i.operands if i.id else []):
   if op.type==capstone.x86.X86_OP_MEM:
    addr=op.mem.disp+(i.address+i.size if op.mem.base==capstone.x86.X86_REG_RIP else 0)
    if addr in imports:annotation+=' ; '+imports[addr]
  lines.append(f'{i.address:016x} file+{pe.get_offset_from_rva(i.address-base):06x}: {i.mnemonic} {i.op_str}{annotation}')
 (pathlib.Path(os.environ['TEMP'])/('palm-'+p.name+'.asm')).write_text('\n'.join(lines),encoding='utf-8')
 out.append('API call contexts:')
 for n,l in enumerate(lines):
  if re.search(r'; (CreateFile|ReadFile|WriteFile|DeviceIoControl|SetupDi|IoRegisterDeviceInterface|IoCreateDevice|IoCreateSymbolicLink)',l):out.extend(lines[max(0,n-12):n+4])
for p in [pathlib.Path('C:/Windows/INF/oem117.inf'),store/'aceecausbdx64.inf',root/'USB_Driver/PalmUSBD.inf']:
 b=p.read_bytes()
 out += [f'## INF {p}',f'SHA256 {hashlib.sha256(b).hexdigest()}']
 # The configuration sections precede the localized string tables.
 for n,line in enumerate(b.decode('cp1252').splitlines(),1):
  if line.strip().lower()=='[strings]': break
  if line.strip() and not line.lstrip().startswith(';'):out.append(f'{n}: {line}')
def registry(path, recursive=True):
 try:
  with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,path,0,winreg.KEY_READ) as k:
   out.append('HKLM\\'+path)
   for n in range(winreg.QueryInfoKey(k)[1]):
    name,value,kind=winreg.EnumValue(k,n)
    out.append(f' {name} type={kind}: {value!r}')
   if recursive:
    for n in range(winreg.QueryInfoKey(k)[0]):registry(path+'\\'+winreg.EnumKey(k,n))
 except OSError as e:out.append(f'HKLM\\{path}: {e}')
registry(r'SYSTEM\CurrentControlSet\Services\AceecaUSBDx64')
with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,r'SYSTEM\CurrentControlSet\Enum\USB') as k:
 for n in range(winreg.QueryInfoKey(k)[0]):
  name=winreg.EnumKey(k,n)
  if re.match('VID_0830|VID_082D',name,re.I):registry('SYSTEM\\CurrentControlSet\\Enum\\USB\\'+name)
for g in ['784126bf-4190-11d4-b5c2-00c04f687a67','d0b3daa1-d88a-11d2-8a30-00500418785b','a5dcbf10-6530-11d2-901f-00c04fb951ed']:
 path='SYSTEM\\CurrentControlSet\\Control\\DeviceClasses\\{'+g+'}'
 with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,path) as k:
  for n in range(winreg.QueryInfoKey(k)[0]):
   name=winreg.EnumKey(k,n)
   if re.search('vid_0830|vid_082d',name,re.I):registry(path+'\\'+name)
args=['pnputil','/enum-devices','/class','{784126c0-4190-11d4-b5c2-00c04f687a67}','/drivers']
out += ['## '+' '.join(args),subprocess.run(args,capture_output=True,text=True,errors='replace').stdout]
print('\n'.join(out))

