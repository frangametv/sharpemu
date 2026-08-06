// Enumerate every Ghidra reference to one GTA V runtime data address.
// @category SharpEmu.Evidence

import java.util.LinkedHashSet;
import java.util.Set;

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;

/**
 * Usage from analyzeHeadless:
 * -postScript TraceGtaDataWriters.java runtime-base runtime-data-address
 */
public class TraceGtaDataWriters extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length != 2) {
            throw new IllegalArgumentException(
                "expected runtime base and runtime data address");
        }

        long runtimeBase = Long.decode(args[0]);
        long runtimeAddress = Long.decode(args[1]);
        Address loadedAddress = currentProgram.getImageBase().add(runtimeAddress - runtimeBase);
        println(String.format(
            "GTA_DATA_XREFS_BEGIN program=%s sha256=%s runtime=0x%016x loaded=%s",
            currentProgram.getName(), currentProgram.getExecutableSHA256(),
            runtimeAddress, loadedAddress));

        Set<Function> owners = new LinkedHashSet<>();
        ReferenceIterator references =
            currentProgram.getReferenceManager().getReferencesTo(loadedAddress);
        int count = 0;
        int writes = 0;
        while (references.hasNext() && !monitor.isCancelled()) {
            Reference reference = references.next();
            Address from = reference.getFromAddress();
            Instruction instruction = currentProgram.getListing().getInstructionContaining(from);
            Function owner = currentProgram.getFunctionManager().getFunctionContaining(from);
            if (owner != null) {
                owners.add(owner);
            }
            boolean write = reference.getReferenceType().isWrite();
            if (write) {
                writes++;
            }
            println(String.format(
                "GTA_DATA_XREF from=%s type=%s read=%s write=%s owner=%s insn=%s",
                from, reference.getReferenceType(), reference.getReferenceType().isRead(), write,
                owner == null ? "<none>" : owner.getName(true) + "@" + owner.getEntryPoint(),
                instruction == null ? "<none>" : instruction.toString()));
            count++;
        }

        DecompInterface decompiler = new DecompInterface();
        try {
            decompiler.openProgram(currentProgram);
            for (Function owner : owners) {
                DecompileResults result = decompiler.decompileFunction(owner, 180, monitor);
                println("GTA_DATA_OWNER_DECOMPILE_BEGIN " + owner.getEntryPoint() +
                    " " + owner.getName(true));
                if (result.decompileCompleted() && result.getDecompiledFunction() != null) {
                    println(result.getDecompiledFunction().getC());
                }
                else {
                    println("<failed> " + result.getErrorMessage());
                }
                println("GTA_DATA_OWNER_DECOMPILE_END " + owner.getEntryPoint());
            }
        }
        finally {
            decompiler.dispose();
        }

        println(String.format(
            "GTA_DATA_XREFS_END references=%d writes=%d owners=%d",
            count, writes, owners.size()));
    }
}
