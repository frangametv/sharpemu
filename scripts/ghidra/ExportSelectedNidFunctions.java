// Export Ghidra function evidence for a selected PS5 NID set.
// @category SharpEmu.Evidence

import java.io.File;
import java.io.FileWriter;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.TreeMap;
import java.util.TreeSet;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonArray;
import com.google.gson.JsonObject;

import ghidra.app.script.GhidraScript;
import ghidra.framework.Application;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.FunctionManager;
import ghidra.program.model.symbol.Symbol;
import ghidra.program.model.symbol.SymbolIterator;

/**
 * Usage from analyzeHeadless:
 * -postScript ExportSelectedNidFunctions.java target-nids.csv evidence.json
 */
public class ExportSelectedNidFunctions extends GhidraScript {
    private static final Pattern NID_PATTERN =
        Pattern.compile("^([A-Za-z0-9+\\-]{11})(?:#.*)?$");

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length != 2) {
            throw new IllegalArgumentException(
                "expected target NID CSV/text path and output JSON path");
        }

        Path targetPath = Path.of(args[0]);
        Path outputPath = Path.of(args[1]);
        Set<String> targets = loadTargets(targetPath);
        if (targets.isEmpty()) {
            throw new IllegalArgumentException("target NID set is empty: " + targetPath);
        }

        FunctionManager functions = currentProgram.getFunctionManager();
        Map<String, JsonObject> matches = new TreeMap<>();
        SymbolIterator symbols = currentProgram.getSymbolTable().getAllSymbols(true);
        while (symbols.hasNext() && !monitor.isCancelled()) {
            Symbol symbol = symbols.next();
            Matcher matcher = NID_PATTERN.matcher(symbol.getName());
            if (!matcher.matches() || !targets.contains(matcher.group(1))) {
                continue;
            }

            String nid = matcher.group(1);
            Function function = functions.getFunctionAt(symbol.getAddress());
            if (function == null) {
                function = functions.getFunctionContaining(symbol.getAddress());
            }

            JsonObject row = new JsonObject();
            row.addProperty("nid", nid);
            row.addProperty("symbol", symbol.getName());
            row.addProperty("symbol_address", symbol.getAddress().toString());
            row.addProperty("symbol_type", symbol.getSymbolType().toString());
            row.addProperty("symbol_source", symbol.getSource().toString());
            row.addProperty("external_entry_point", symbol.isExternalEntryPoint());
            row.addProperty("global", symbol.isGlobal());
            row.addProperty("function_present", function != null);
            if (function != null) {
                row.addProperty("function_name", function.getName());
                row.addProperty("function_entry", function.getEntryPoint().toString());
                row.addProperty("function_min_address", function.getBody().getMinAddress().toString());
                row.addProperty("function_max_address", function.getBody().getMaxAddress().toString());
                row.addProperty("function_body_addresses", function.getBody().getNumAddresses());
                row.addProperty("calling_convention", function.getCallingConventionName());
                row.addProperty("signature", function.getSignature().toString());
                row.addProperty("thunk", function.isThunk());
            }

            JsonObject previous = matches.get(nid);
            if (previous == null ||
                (!previous.get("function_present").getAsBoolean() && function != null)) {
                matches.put(nid, row);
            }
        }

        Set<String> missing = new TreeSet<>(targets);
        missing.removeAll(matches.keySet());
        List<String> withoutFunctions = new ArrayList<>();
        for (Map.Entry<String, JsonObject> entry : matches.entrySet()) {
            if (!entry.getValue().get("function_present").getAsBoolean()) {
                withoutFunctions.add(entry.getKey());
            }
        }

        JsonObject result = new JsonObject();
        result.addProperty("format", "sharpemu-ghidra-selected-nid-functions-v1");
        result.addProperty("ghidra_version", Application.getApplicationVersion());
        result.addProperty("program_name", currentProgram.getName());
        result.addProperty("program_path", currentProgram.getDomainFile().getPathname());
        result.addProperty("executable_format", currentProgram.getExecutableFormat());
        result.addProperty("executable_sha256", currentProgram.getExecutableSHA256());
        result.addProperty("image_base", currentProgram.getImageBase().toString());
        result.addProperty("language_id", currentProgram.getLanguageID().toString());
        result.addProperty("target_path", targetPath.toAbsolutePath().normalize().toString());
        result.addProperty("target_count", targets.size());
        result.addProperty("matched_count", matches.size());
        result.addProperty("function_count", matches.size() - withoutFunctions.size());
        result.add("missing", strings(missing));
        result.add("without_functions", strings(withoutFunctions));
        JsonArray matchedFunctions = new JsonArray();
        for (JsonObject row : matches.values()) {
            matchedFunctions.add(row);
        }
        result.add("functions", matchedFunctions);

        Path parent = outputPath.toAbsolutePath().normalize().getParent();
        if (parent != null) {
            Files.createDirectories(parent);
        }
        Gson gson = new GsonBuilder().setPrettyPrinting().create();
        try (FileWriter writer = new FileWriter(outputPath.toFile(), StandardCharsets.UTF_8)) {
            gson.toJson(result, writer);
            writer.write(System.lineSeparator());
        }

        println(String.format(
            "SHARPEMU_NID_EVIDENCE targets=%d matched=%d functions=%d missing=%d output=%s",
            targets.size(), matches.size(), matches.size() - withoutFunctions.size(),
            missing.size(), outputPath));
        if (!missing.isEmpty() || !withoutFunctions.isEmpty()) {
            throw new IllegalStateException(String.format(
                "Ghidra evidence incomplete: missing=%d without_functions=%d",
                missing.size(), withoutFunctions.size()));
        }
    }

    private static JsonArray strings(Iterable<String> values) {
        JsonArray result = new JsonArray();
        for (String value : values) {
            result.add(value);
        }
        return result;
    }

    private static Set<String> loadTargets(Path path) throws Exception {
        List<String> lines = Files.readAllLines(path, StandardCharsets.UTF_8);
        Set<String> targets = new TreeSet<>();
        if (lines.isEmpty()) {
            return targets;
        }

        List<String> header = parseCsvLine(lines.get(0));
        int nidColumn = header.indexOf("nid");
        int start = nidColumn >= 0 ? 1 : 0;
        for (int index = start; index < lines.size(); index++) {
            List<String> columns = parseCsvLine(lines.get(index));
            String value;
            if (nidColumn >= 0) {
                if (nidColumn >= columns.size()) {
                    continue;
                }
                value = columns.get(nidColumn).trim();
            }
            else {
                value = columns.isEmpty() ? "" : columns.get(0).trim();
            }
            if (NID_PATTERN.matcher(value).matches()) {
                targets.add(value);
            }
        }
        return targets;
    }

    private static List<String> parseCsvLine(String line) {
        if (line.indexOf(',') < 0 && line.indexOf('"') < 0) {
            return Collections.singletonList(line);
        }
        List<String> values = new ArrayList<>();
        StringBuilder value = new StringBuilder();
        boolean quoted = false;
        for (int index = 0; index < line.length(); index++) {
            char ch = line.charAt(index);
            if (ch == '"') {
                if (quoted && index + 1 < line.length() && line.charAt(index + 1) == '"') {
                    value.append('"');
                    index++;
                }
                else {
                    quoted = !quoted;
                }
            }
            else if (ch == ',' && !quoted) {
                values.add(value.toString());
                value.setLength(0);
            }
            else {
                value.append(ch);
            }
        }
        values.add(value.toString());
        return values;
    }
}
