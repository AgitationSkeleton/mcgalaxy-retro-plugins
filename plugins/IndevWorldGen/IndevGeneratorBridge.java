import java.io.BufferedOutputStream;
import java.io.DataOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.lang.reflect.Proxy;
import java.net.URL;
import java.net.URLClassLoader;
import java.util.Random;

public final class IndevGeneratorBridge {
    private static int intField(Object value, String name) throws Exception {
        Field field = value.getClass().getField(name);
        return field.getInt(value);
    }

    public static void main(String[] args) throws Exception {
        if (args.length != 9) {
            throw new IllegalArgumentException("jar output width length height type theme seed name");
        }

        File gameJar = new File(args[0]);
        File output = new File(args[1]);
        int width = Integer.parseInt(args[2]);
        int length = Integer.parseInt(args[3]);
        int height = Integer.parseInt(args[4]);
        int type = Integer.parseInt(args[5]);
        int theme = Integer.parseInt(args[6]);
        long seed = Long.parseLong(args[7]);
        String name = args[8];

        URLClassLoader loader = new URLClassLoader(new URL[] { gameJar.toURI().toURL() }, IndevGeneratorBridge.class.getClassLoader());
        Class<?> progressType = loader.loadClass("a.b");
        Object progress = Proxy.newProxyInstance(loader, new Class<?>[] { progressType }, (proxy, method, values) -> null);

        Class<?> generatorType = loader.loadClass("net.minecraft.a.a.c.a");
        Constructor<?> constructor = generatorType.getConstructor(progressType);
        Object generator = constructor.newInstance(progress);
        generatorType.getField("a").setBoolean(generator, type == 1); // Island
        generatorType.getField("b").setBoolean(generator, type == 2); // Floating
        generatorType.getField("c").setBoolean(generator, type == 3); // Flat
        generatorType.getField("d").setInt(generator, theme);

        Field random = generatorType.getDeclaredField("i");
        random.setAccessible(true);
        random.set(generator, new Random(seed));

        Method generate = generatorType.getMethod("a", String.class, int.class, int.class, int.class);
        Object level = generate.invoke(generator, name, width, length, height);
        byte[] blocks = (byte[])level.getClass().getField("d").get(level);

        try (DataOutputStream out = new DataOutputStream(new BufferedOutputStream(new FileOutputStream(output)))) {
            out.writeInt(0x494E4456); // INDV
            out.writeInt(1);
            out.writeInt(intField(level, "a"));
            out.writeInt(intField(level, "c"));
            out.writeInt(intField(level, "b"));
            out.writeInt(intField(level, "i"));
            out.writeInt(intField(level, "j"));
            out.writeInt(intField(level, "k"));
            out.writeInt(intField(level, "s"));
            out.writeInt(intField(level, "t"));
            out.writeInt(intField(level, "u"));
            out.writeInt(intField(level, "v"));
            out.writeInt(intField(level, "w"));
            out.writeInt(intField(level, "x"));
            out.writeInt(intField(level, "m"));
            out.writeInt(intField(level, "A"));
            out.writeInt(intField(level, "B"));
            out.writeInt(blocks.length);
            out.write(blocks);
        }
        loader.close();
    }
}
